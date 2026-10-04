using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeshCoreMessenger.Desktop.Presentation;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreMessenger.Desktop.Views.Chat;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    public static class NativeChannelRepeatAudit
    {
        public static async Task RunAsync()
        {
            var output = Path.Combine(Path.GetTempPath(), "meshcore-channel-repeat"); Directory.CreateDirectory(output);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (var width in new[] { 420, 960 })
            {
                await using var w = await Workspace.CreateAsync(); await w.Root.StopAsync();
                var sender = new UiChannelSender(w);
                w.Root = w.CreateRoot(dispatcher: new NativeDispatcher(), messageService: sender); await w.Root.LoadAsync(); await OnlineSender(w);
                var workspace = w.Root.Chats.Public; var composer = workspace.Composer;
                var view = new ConversationView { DataContext = workspace };
                var window = new Window { Content = view, Width = width, Height = 700, RequestedThemeVariant = theme };
                window.Show(); window.Activate();
                try
                {
                    await SettleAsync(window); await workspace.Navigation.History.JumpToLatestAsync();
                    composer.Text = "Повтор без подтверждения 👋"; await UntilAsync(() => composer.CanSend);
                    await composer.SendCommand.ExecuteAsync(null);
                    await UntilAsync(() => workspace.Navigation.Messages.Any(m => m.Body == "Повтор без подтверждения 👋" && m.CanRetry));
                    await SettleAsync(window);
                    var bubble = workspace.Navigation.Messages.Single(m => m.Body == "Повтор без подтверждения 👋");
                    composer.Text = "Черновик остаётся";
                    var message = view.GetVisualDescendants().OfType<MessageView>().Single(v => ReferenceEquals(v.DataContext, bubble));
                    var anchor = message.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Classes.Contains("message"));
                    var menu = anchor.ContextMenu!;
                    menu.Open(anchor); await SettleAsync(window);
                    var retry = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Повторить доставку"));
                    if (!retry.IsVisible || !retry.IsEnabled) throw new InvalidOperationException("Channel repeat menu binding failed.");
                    retry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.Close();
                    await UntilAsync(() => bubble.Presentation.AttemptNumber == 2 && bubble.Presentation.State == MessageSendDisplayState.AcceptedByNode);
                    await SettleAsync(window);
                    if (sender.Calls != 2 || window.OwnedWindows.Count != 0 || composer.Text != "Черновик остаётся" ||
                        workspace.Navigation.Messages.Count(m => m.Id == bubble.Id) != 1)
                        throw new InvalidOperationException("Repeat required a dialog, changed the draft, or duplicated the bubble.");
                    menu.Open(anchor); await SettleAsync(window);
                    var asNew = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Отправить как новое"));
                    if (!asNew.IsVisible || !asNew.IsEnabled) throw new InvalidOperationException("Send as new menu binding failed.");
                    asNew.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.Close();
                    await UntilAsync(() => workspace.Navigation.Messages.Count(m => m.Body == bubble.Body) == 2);
                    await SettleAsync(window);
                    if (sender.Calls != 3 || window.OwnedWindows.Count != 0 || composer.Text != "Черновик остаётся" ||
                        bubble.Presentation.AttemptNumber != 2)
                        throw new InvalidOperationException("Send as new did not preserve source or draft.");
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height));
                    bitmap.Render(window); bitmap.Save(Path.Combine(output, $"{theme}-{width}.png"), PngBitmapEncoderOptions.Default);
                    Console.WriteLine($"Channel repeat/{theme}/{width}: context menu, no confirmation, delivery repeat keeps one bubble; send as new adds a bubble; draft retained.");
                }
                finally { await w.Root.StopAsync(); window.Close(); }
            }
        }
    }
}
