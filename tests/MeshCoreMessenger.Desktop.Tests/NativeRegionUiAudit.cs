using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreMessenger.Desktop.Views;
using MeshCoreMessenger.Desktop.Views.Settings;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    // Explicit isolated fixture audit; no physical transport or user's database.
    public static class NativeRegionUiAudit
    {
        public static async Task RunAsync()
        {
            await using var f = await Workspace.CreateAsync(); await f.Root.StopAsync();
            f.Root = f.CreateRoot(dispatcher: new NativeDispatcher(), includeRegions: true);
            var root = f.Root; await root.LoadAsync();
            var service = new RegionService(f.Storage.Regions, TimeProvider.System);
            await service.CreatePublicAsync(f.A.NodeId, "Ростов", "ru-ros");
            await root.Regions.RefreshAsync();
            var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "region-ui-native"); Directory.CreateDirectory(output);
            var window = new MainWindow { DataContext = root, Height = 860 };
            try
            {
                window.Show();
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                foreach (var width in new[] { 560, 1040 })
                {
                    window.RequestedThemeVariant = theme; window.Width = width;
                    root.Shell.SelectSection(ShellSection.Settings); await SettleAsync(window);
                    var view = window.GetVisualDescendants().OfType<RegionSettingsView>().Single();
                    var input = view.GetVisualDescendants().OfType<Button>().Single(t => Equals(t.Content, "Поиск регионов"));
                    // Offline search is disabled; the catalogue toggle remains keyboard accessible.
                    var toggle = view.GetVisualDescendants().OfType<Button>().Single(t => Equals(t.Content, "−"));
                    toggle.Focus(); toggle.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab });
                    var card = view.GetVisualDescendants().OfType<StackPanel>().First(p => p.DataContext is RegionCatalogSearchViewModel);
                    if (card.GetVisualDescendants().OfType<TextBox>().Any() || card.GetVisualDescendants().OfType<CheckBox>().Any())
                        throw new InvalidOperationException("The simple region card must not contain technical inputs.");
                    if (window.FocusManager?.GetFocusedElement() is not Control) throw new InvalidOperationException("Region settings keyboard focus was lost.");
                    foreach (var control in view.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c is TextBox or ComboBox or Button))
                    {
                        var origin = control.TranslatePoint(default, view);
                        if (origin is { } p && (p.X < -1 || p.X + control.Bounds.Width > view.Bounds.Width + 2))
                            throw new InvalidOperationException($"Region control overflows settings at width{width}: {control.GetType().Name}.");
                    }
                    Save("settings");
                    root.Shell.SelectSection(ShellSection.PublicChats);
                    await root.Chats.Public.SelectConversationAsync(root.Chats.Public.SelectedConversation);
                    await SettleAsync(window);
                    await root.Chats.Public.Regions!.RefreshAsync();
                    if (!window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text?.StartsWith("Регион: ") == true))
                        throw new InvalidOperationException("Channel region header is not visible.");
                    var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConversationMenuButton" && b.IsEffectivelyVisible);
                    var flyout = (MenuFlyout)button.Flyout!; flyout.ShowAt(button); await SettleAsync(window);
                    // Avalonia keeps its popup private; inspect only in this native test harness.
                    var popup = typeof(PopupFlyoutBase).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .Select(field => field.GetValue(flyout)).Concat(typeof(PopupFlyoutBase)
                            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                            .Where(property => property.PropertyType == typeof(Popup)).Select(property => property.GetValue(flyout)))
                        .OfType<Popup>().First();
                    var presenter = (ItemsControl)popup.Child!;
                    var parent = (MenuItem)presenter.ContainerFromIndex(1)!;
                    if (!Equals(parent.Header, "Регион отправки")) throw new InvalidOperationException("Region submenu header binding failed.");
                    parent.IsSubMenuOpen = true; await SettleAsync(window);
                    var choice = (MenuItem)parent.ContainerFromIndex(2)!;
                    if (choice.Header?.ToString()?.Contains("ru-ros") != true || choice.Command is not CommunityToolkit.Mvvm.Input.IAsyncRelayCommand command)
                        throw new InvalidOperationException("Nested region menu bindings failed.");
                    await command.ExecuteAsync(null); flyout.Hide();
                    await UntilAsync(() => root.Chats.Public.Regions.Text == "Регион: ru-ros");
                    Save("channel");
                    Console.WriteLine($"REGION native {theme}/{width}: Settings cards, focus, bounds, channel header and local menu assignment passed.");
                    void Save(string page)
                    {
                        using var image = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height));
                        image.Render(window); image.Save(Path.Combine(output, $"{page}-{theme}-{width}.png"), PngBitmapEncoderOptions.Default);
                    }
                }
                if (f.Supervisor.ConnectCalls != 0) throw new InvalidOperationException("Region UI audit must not connect to hardware.");
            }
            finally { window.Close(); }
        }
    }
}
