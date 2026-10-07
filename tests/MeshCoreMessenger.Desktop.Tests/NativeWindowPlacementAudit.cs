using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Preferences;
using MeshCoreMessenger.Desktop.Views;
using Avalonia.Controls;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    public static class NativeWindowPlacementAudit
    {
        public static async Task RunAsync()
        {
            await using var workspace = await Workspace.CreateAsync();
            await workspace.Root.StopAsync();
            var root = workspace.CreateRoot(dispatcher: new AvaloniaUiDispatcher()); workspace.Root = root;
            await root.LoadAsync();
            workspace.Preferences.SetWindowPlacement(new(0, 33, 2080, 1400, true));
            var window = new MainWindow { DataContext = root };
            try
            {
                window.Show(); await SettleAsync(window);
                Print("restored-max");
                window.Width = 800; window.Height = 500;
                window.Position = new(100, 120);
                await SettleAsync(window);
                Print("manual-size");
                if (OperatingSystem.IsMacOS() && window.WindowState != WindowState.Normal)
                    throw new InvalidOperationException("Manual macOS resize did not leave zoomed state.");
                window.WindowState = WindowState.Normal;
                window.Width = 820; window.Height = 520; window.Position = new(130, 150);
                await SettleAsync(window);
                Print("normal-size");
                var expected = root.SavedWindowPlacement;
                var screenScale = window.Screens.ScreenFromWindow(window)!.Scaling;
                if (expected is null || expected.IsMaximized || Math.Abs(expected.Width - 820 * screenScale) > 2 ||
                    Math.Abs(expected.Height - 520 * screenScale) > 2)
                    throw new InvalidOperationException("Captured placement mixes screen coordinates and backing pixels.");
                await workspace.Preferences.FlushAsync();
                var persisted = new DesktopPreferences(workspace.Storage.Settings);
                await persisted.LoadAsync();
                if (persisted.Snapshot.WindowPlacement != expected) throw new InvalidOperationException("SQLite placement differs from captured geometry.");
                window.Close();
                if (root.SavedWindowPlacement != expected) throw new InvalidOperationException("Window teardown overwrote the saved placement.");
                var restarted = new MainWindow { DataContext = root };
                restarted.Show(); await SettleAsync(restarted);
                Console.WriteLine($"restarted: state={restarted.WindowState} position={restarted.Position} bounds={restarted.Bounds} scale={restarted.RenderScaling} saved={root.SavedWindowPlacement}");
                if (restarted.WindowState != WindowState.Normal || Math.Abs(restarted.Bounds.Width - 820) > 2 ||
                    Math.Abs(restarted.Bounds.Height - 520) > 2 || restarted.Position != new Avalonia.PixelPoint(130, 150))
                    throw new InvalidOperationException("Native restart failed to restore normal size and position.");
                restarted.Close();
                Console.WriteLine("Placement native roundtrip: manual resize after Max, exact screen units, SQLite persistence and restarted normal bounds/position passed.");
                void Print(string label) => Console.WriteLine($"{label}: state={window.WindowState} position={window.Position} bounds={window.Bounds} scale={window.RenderScaling} saved={root.SavedWindowPlacement}; screens=" + string.Join(";",window.Screens.All.Select(s => $"work={s.WorkingArea} scale={s.Scaling}")));
            }
            finally { window.Close(); }
        }
    }
}
