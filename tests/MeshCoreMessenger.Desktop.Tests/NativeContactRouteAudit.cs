using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreMessenger.Desktop.Views.Chat;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    public static class NativeContactRouteAudit
    {
        public static async Task RunAsync()
        {
            var output = Path.Combine(Path.GetTempPath(), "meshcore-route-reset"); Directory.CreateDirectory(output);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (var width in new[] { 420, 960 })
            {
                await using var w = await Workspace.CreateAsync(); await w.Root.StopAsync();
                var session = await OnlineSender(w);
                var key = Enumerable.Repeat((byte)9, 32).ToArray();
                await w.Storage.Directories.UpdateContactRouteAsync(w.A.NodeId, session, key, new byte[64], 2, DateTimeOffset.UtcNow);
                var service = new UiRouteService(w);
                w.Root = w.CreateRoot(dispatcher: new NativeDispatcher(), contactRoutes: service);
                await w.Root.LoadAsync();
                w.Root.Shell.SelectSection(ShellSection.PrivateChats);
                var workspace = w.Root.Chats.Private;
                var view = new ConversationView { DataContext = workspace };
                var window = new Window { Content = view, Width = width, Height = 700, RequestedThemeVariant = theme, Title = "Маршрут — UI эмуляция, временная SQLite" };
                window.Show(); window.Activate();
                try
                {
                    await SettleAsync(window);
                    workspace.Navigation.Draft.Text = "Черновик не отправляется 👋";
                    var messages = workspace.Navigation.Messages.Select(item => item.Id).ToArray();
                    var button = view.FindControl<Button>("ConversationMenuButton")!;
                    var flyout = (MenuFlyout)button.Flyout!;
                    flyout.ShowAt(button);
                    await UntilAsync(() => workspace.RouteReset.CanReset);
                    await SettleAsync(window);
                    var popup = typeof(PopupFlyoutBase).GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                        .Where(property => typeof(Popup).IsAssignableFrom(property.PropertyType))
                        .Select(property => property.GetValue(flyout)).OfType<Popup>().Single();
                    var item = popup.Child!.GetLogicalDescendants().OfType<MenuItem>().Single(i => Equals(i.Header, "Сбросить маршрут"));
                    if (!item.IsEffectivelyEnabled || !ReferenceEquals(item.Command, workspace.RouteReset.Command)) throw new InvalidOperationException("Reset menu binding failed.");
                    item.Command!.Execute(item.CommandParameter);
                    flyout.Hide();
                    await workspace.RouteReset.Command.ExecutionTask!;
                    await SettleAsync(window);
                    if (service.Calls != 1 || workspace.RouteReset.StatusMessage != "Маршрут сброшен." ||
                        workspace.Navigation.SelectedMetadata != "Маршрут: широковещательный" ||
                        !messages.SequenceEqual(workspace.Navigation.Messages.Select(message => message.Id)) ||
                        workspace.Navigation.Draft.Text != "Черновик не отправляется 👋") throw new InvalidOperationException("Reset metadata/history/draft audit failed.");
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height));
                    bitmap.Render(window); bitmap.Save(Path.Combine(output, $"reset-{theme}-{width}.png"), PngBitmapEncoderOptions.Default);
                    Console.WriteLine($"Route reset native Private {theme} {width}: command once, flood metadata, history/draft retained.");
                }
                finally { window.Close(); }
            }
            Console.WriteLine($"Route reset screenshots: {output}");
        }
        private sealed class UiRouteService(Workspace w) : IContactRouteService
        {
            public int Calls { get; private set; }
            public Task<string?> GetUnavailableReasonAsync(ContactRouteResetRequest request, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
            public async Task<ContactRouteResetResult> ResetAsync(ContactRouteResetRequest request, CancellationToken cancellationToken = default)
            {
                Calls++;
                await w.Storage.Directories.UpdateContactRouteAsync(request.NodeId, request.SessionId, request.PublicKey, new byte[64], 0xFF, DateTimeOffset.UtcNow, cancellationToken);
                return new(request.NodeId, request.PublicKey, true, null);
            }
        }
    }
}
