using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Preferences;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreMessenger.Desktop.Views;
using MeshCoreMessenger.Desktop.Views.Settings;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    public static class NativeSettingsPreferencesAudit
    {
        public static async Task RunAsync()
        {
            await using var workspace = await Workspace.CreateAsync();
            await workspace.Root.StopAsync();
            var store = new AuditPreferencesStore(workspace.Storage.Settings);
            var root = workspace.CreateRoot(dispatcher: new AvaloniaUiDispatcher(), preferencesStore: store);
            workspace.Root = root;
            await root.LoadAsync();
            root.Shell.SelectSection(ShellSection.Settings);
            var output = Path.Combine(Path.GetTempPath(), "meshcore-s2-settings");
            Directory.CreateDirectory(output);
            var window = new MainWindow { DataContext = root, Height = 720 };
            try
            {
                window.Show();
                await SettleAsync(window);
                var view = window.GetVisualDescendants().OfType<ApplicationSettingsView>().Single();
                var close = view.GetVisualDescendants().OfType<ComboBox>()
                    .Single(combo => ReferenceEquals(combo.ItemsSource, root.CloseBehaviorOptions));
                var checks = view.GetVisualDescendants().OfType<CheckBox>().ToArray();
                if (checks.Length != 2) throw new InvalidOperationException("Expected exactly two notification checkboxes.");
                foreach (var theme in new[] { DesktopThemePreference.Light, DesktopThemePreference.Dark })
                foreach (var width in new[] { 560, 960 })
                {
                    window.Width = width;
                    root.SelectedTheme = root.ThemeOptions.Single(option => option.Value == theme);
                    var enabled = width == 960;
                    close.SelectedItem = root.CloseBehaviorOptions.Single(option => option.Value ==
                        (enabled ? DesktopCloseBehavior.MinimizeToTray : DesktopCloseBehavior.ExitApplication));
                    checks[0].IsChecked = enabled;
                    checks[1].IsChecked = enabled;
                    await WaitForValuesAsync();
                    await SettleAsync(window);
                    if (Math.Abs(window.ClientSize.Width - width) > 1)
                        throw new InvalidOperationException("The native window width did not match the requested scenario.");
                    foreach (var control in new Control[] { close, checks[0], checks[1] })
                    {
                        var origin = control.TranslatePoint(default, view)!.Value;
                        if (origin.X < 0 || origin.X + control.Bounds.Width > view.Bounds.Width + 1)
                            throw new InvalidOperationException("Settings controls overflow the narrow page.");
                    }
                    close.Focus();
                    close.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab });
                    if (!ReferenceEquals(window.FocusManager?.GetFocusedElement(), checks[0]))
                        throw new InvalidOperationException("Tab did not move from the close setting to private notifications.");
                    var wasChecked = checks[0].IsChecked;
                    checks[0].RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
                    checks[0].RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Space });
                    if (checks[0].IsChecked == wasChecked || root.NotifyPrivateMessages != checks[0].IsChecked)
                        throw new InvalidOperationException("Space did not toggle and bind private notifications.");
                    await WaitForValuesAsync();
                    SaveImage($"{theme}-{width}");
                    Console.WriteLine($"S2 native {theme}/{width}: bindings, background SQLite save, Tab/Space and bounds passed.");
                }

                store.Fail = true;
                checks[1].IsChecked = !root.NotifyChannelMessages;
                for (var i = 0; !root.HasPreferencesSaveError && i < 100; i++) await Task.Delay(10);
                if (!root.HasPreferencesSaveError) throw new InvalidOperationException("Settings write failure was hidden.");
                await SettleAsync(window);
                var retry = view.GetVisualDescendants().OfType<Button>().Single(button =>
                    ReferenceEquals(button.Command, root.RetryPreferencesSaveCommand));
                if (!retry.IsEffectivelyVisible || !retry.IsEnabled) throw new InvalidOperationException("Settings retry is unavailable.");
                var scroller = view.GetVisualDescendants().OfType<ScrollViewer>()
                    .Single(control => control.Classes.Contains("application-settings"));
                scroller.Offset = new Vector(0, scroller.Extent.Height);
                await SettleAsync(window);
                SaveImage("Dark-960-save-error");
                store.Fail = false;
                await root.RetryPreferencesSaveCommand.ExecuteAsync(null);
                await WaitForValuesAsync();
                if (root.HasPreferencesSaveError) throw new InvalidOperationException("Retry did not clear the settings error.");
                if (workspace.Supervisor.ConnectCalls != 0 || workspace.Supervisor.DisconnectCalls != 0)
                    throw new InvalidOperationException("Saving preferences called connection commands.");
                Console.WriteLine($"S2 native save failure/retry passed; zero connection commands. Images: {output}");

                async Task WaitForValuesAsync()
                {
                    for (var i = 0; i < 100; i++)
                    {
                        var loaded = new DesktopPreferences(workspace.Storage.Settings);
                        await loaded.LoadAsync();
                        var snapshot = loaded.Snapshot;
                        if (snapshot.Theme == root.SelectedTheme.Value && snapshot.CloseBehavior == root.SelectedCloseBehavior.Value &&
                            snapshot.NotifyPrivateMessages == root.NotifyPrivateMessages && snapshot.NotifyChannelMessages == root.NotifyChannelMessages)
                            return;
                        await Task.Delay(10);
                    }
                    throw new InvalidOperationException("Settings were not saved before application exit.");
                }
                void SaveImage(string name)
                {
                    using var image = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height));
                    image.Render(window);
                    image.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
                }
            }
            finally { window.Close(); }
        }

        private sealed class AuditPreferencesStore(ISettingsStore inner) : ISettingsStore
        {
            public bool Fail { get; set; }
            public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => inner.GetAsync(key, cancellationToken);
            public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) => Fail
                ? Task.FromException(new IOException("Synthetic settings failure")) : inner.SetAsync(key, value, cancellationToken);
        }
    }
}
