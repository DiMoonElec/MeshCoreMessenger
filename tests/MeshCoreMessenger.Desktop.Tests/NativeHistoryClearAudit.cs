using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using System.Reflection;
using MeshCoreMessenger.Desktop.ViewModels;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.LogicalTree;
using MeshCoreMessenger.Desktop.Views.Chat;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    public static class NativeHistoryClearAudit
    {
        public static async Task RunAsync()
        {
            var output = Path.Combine(Path.GetTempPath(), "meshcore-history-clear");
            Directory.CreateDirectory(output);
            foreach (var privateChat in new[] { false, true })
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                foreach (var width in new[] { 420, 960 })
                {
                    await using var w = await Workspace.CreateAsync();
                    await w.Root.StopAsync();
                    w.Root = w.CreateRoot(dispatcher: new NativeDispatcher());
                    await w.Root.LoadAsync();
                    if (privateChat) w.Root.Shell.SelectSection(MeshCoreMessenger.Desktop.ViewModels.ShellSection.PrivateChats);
                    var workspace = privateChat ? w.Root.Chats.Private : w.Root.Chats.Public;
                    var section = privateChat ? "Private" : "Public";
                    workspace.Navigation.Draft.Text = "Черновик сохраняется 👋";
                    var view = new ConversationView { DataContext = workspace };
                    var window = new Window { Content = view, Width = width, Height = 700, RequestedThemeVariant = theme, Title = "Очистка — временная SQLite" };
                    window.Show(); window.Activate();
                    try
                    {
                        await SettleAsync(window);
                        var button = view.FindControl<Button>("ConversationMenuButton")!;
                        var flyout = (MenuFlyout)button.Flyout!;
                        flyout.ShowAt(button);
                        await UntilAsync(() => workspace.HistoryClear.CanClear);
                        await SettleAsync(window);
                        var popup = typeof(PopupFlyoutBase).GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                            .Where(property => typeof(Popup).IsAssignableFrom(property.PropertyType))
                            .Select(property => property.GetValue(flyout)).OfType<Popup>().Single();
                        var menuItems = popup.Child!.GetLogicalDescendants().OfType<MenuItem>().ToArray();
                        if (menuItems.Length != (privateChat ? 3 : 2) ||
                            menuItems.Any(i => Equals(i.Header, "Сбросить маршрут")) != privateChat)
                            throw new InvalidOperationException("Public/Private menu composition failed.");
                        if (privateChat && menuItems.Single(i => Equals(i.Header, "Сбросить маршрут")).IsEffectivelyEnabled)
                            throw new InvalidOperationException("Offline route reset must be disabled.");
                        var item = menuItems.Single(i => Equals(i.Header, "Удалить историю сообщений"));
                        if (!ReferenceEquals(item.Command, workspace.Menu.Items.Single(i => i.Action == ConversationMenuAction.ClearHistory).Command))
                            throw new InvalidOperationException("Menu command binding failed.");
                        if (!item.IsEffectivelyEnabled) throw new InvalidOperationException("Clear menu binding failed.");
                        item.Command!.Execute(item.CommandParameter);
                        await UntilAsync(() => window.OwnedWindows.OfType<HistoryClearDialog>().Any());
                        var dialog = window.OwnedWindows.OfType<HistoryClearDialog>().Single();
                        await SettleAsync(dialog);
                        if (!dialog.FindControl<Button>("CancelButton")!.IsFocused) throw new InvalidOperationException("Confirmation did not focus Cancel.");
                        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)dialog.ClientSize.Width, (int)dialog.ClientSize.Height)))
                        {
                            bitmap.Render(dialog); bitmap.Save(Path.Combine(output, $"confirm-{section}-{theme}-{width}.png"), PngBitmapEncoderOptions.Default);
                        }
                        dialog.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await SettleAsync(window);
                        if (workspace.Navigation.Messages.Count != 12) throw new InvalidOperationException("Cancel deleted history.");
                        flyout.Hide(); flyout.ShowAt(button);
                        await UntilAsync(() => workspace.HistoryClear.CanClear);
                        item.Command!.Execute(item.CommandParameter);
                        await UntilAsync(() => window.OwnedWindows.OfType<HistoryClearDialog>().Any());
                        dialog = window.OwnedWindows.OfType<HistoryClearDialog>().Single();
                        await SettleAsync(dialog);
                        dialog.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Удалить")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await UntilAsync(() => !workspace.HistoryClear.IsBusy && !workspace.Navigation.History.HasMessages);
                        await SettleAsync(window);
                        if (workspace.HistoryClear.HasError || workspace.Navigation.Draft.Text != "Черновик сохраняется 👋")
                            throw new InvalidOperationException("Clear/error/draft audit failed.");
                        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height)))
                        {
                            bitmap.Render(window); bitmap.Save(Path.Combine(output, $"empty-{section}-{theme}-{width}.png"), PngBitmapEncoderOptions.Default);
                        }
                        Console.WriteLine($"History clear native {section} {theme} {width}: menu enabled, confirmation cancel preserves history, confirm clears, draft retained, no connection");
                    }
                    finally { await w.Root.StopAsync(); window.Close(); }
                }
            Console.WriteLine($"History clear screenshots: {output}");
        }
    }
}
