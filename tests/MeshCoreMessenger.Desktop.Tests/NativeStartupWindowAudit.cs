using Avalonia.Controls;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Preferences;
using MeshCoreMessenger.Desktop.Views;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    public static class NativeStartupWindowAudit
    {
        public static async Task RunAsync()
        {
            await using var workspace = await Workspace.CreateAsync();
            await workspace.Root.StopAsync();
            var root = workspace.CreateRoot(dispatcher: new AvaloniaUiDispatcher()); workspace.Root = root;
            await root.LoadAsync();
            foreach (var (mode, last, trayAvailable, expected) in new[] {
                (DesktopStartupWindowMode.LastState, DesktopWindowPresentation.Open, true, DesktopWindowPresentation.Open),
                (DesktopStartupWindowMode.LastState, DesktopWindowPresentation.Minimized, true, DesktopWindowPresentation.Minimized),
                (DesktopStartupWindowMode.LastState, DesktopWindowPresentation.Tray, true, DesktopWindowPresentation.Tray),
                (DesktopStartupWindowMode.Minimized, DesktopWindowPresentation.Tray, true, DesktopWindowPresentation.Minimized),
                (DesktopStartupWindowMode.Tray, DesktopWindowPresentation.Open, true, DesktopWindowPresentation.Tray),
                (DesktopStartupWindowMode.Tray, DesktopWindowPresentation.Open, false, DesktopWindowPresentation.Minimized),
            })
            {
                root.SelectedStartupWindowMode = root.StartupWindowModeOptions.Single(option => option.Value == mode);
                root.UpdateWindowPresentation(last);
                var window = new MainWindow { DataContext = root, Width = 820, Height = 520 };
                window.ConfigureTrayLifecycle(() => trayAvailable, () => { });
                window.ConfigureStartupPresentation();
                try
                {
                    window.Show(); await SettleAsync(window);
                    var actual = !window.IsVisible ? DesktopWindowPresentation.Tray : window.WindowState == WindowState.Minimized
                        ? DesktopWindowPresentation.Minimized : DesktopWindowPresentation.Open;
                    if (actual != expected || root.LastWindowPresentation != expected)
                        throw new InvalidOperationException($"Startup {mode}/{last}/{trayAvailable}: expected {expected}, actual {actual}, saved {root.LastWindowPresentation}.");
                    await workspace.Preferences.FlushAsync();
                    var persisted = new DesktopPreferences(workspace.Storage.Settings); await persisted.LoadAsync();
                    if (persisted.Snapshot.StartupWindowMode != mode || persisted.Snapshot.LastWindowPresentation != expected)
                        throw new InvalidOperationException("Startup preferences did not persist in SQLite.");
                    using var activation = new DesktopActivationCoordinator(new AvaloniaUiDispatcher());
                    activation.Attach(window.TryActivateExistingWindow);
                    if (!await activation.RequestAsync() || !window.IsVisible || window.WindowState == WindowState.Minimized)
                        throw new InvalidOperationException("Activation did not override startup preference.");
                    await SettleAsync(window);
                    if (root.LastWindowPresentation != DesktopWindowPresentation.Open)
                        throw new InvalidOperationException("Restored state was not captured.");
                    Console.WriteLine($"Startup native: {mode}/{last}, tray={trayAvailable} -> {expected}; SQLite and activation OK.");
                }
                finally { window.Close(); }
            }
            if (workspace.Supervisor.ConnectCalls != 0) throw new InvalidOperationException("Startup UI changed connection policy.");
        }
    }
}
