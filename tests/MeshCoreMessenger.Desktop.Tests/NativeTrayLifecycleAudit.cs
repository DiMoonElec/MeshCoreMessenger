using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreMessenger.Desktop.Preferences;
using MeshCoreMessenger.Desktop.Views;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    public static class NativeTrayLifecycleAudit
    {
        public static async Task RunAsync()
        {
            await using var workspace = await Workspace.CreateAsync(publicMessageCount: 40, variableMessageHeight: true);
            await workspace.Root.StopAsync();
            var root = workspace.CreateRoot(dispatcher: new AvaloniaUiDispatcher());
            workspace.Root = root;
            await root.LoadAsync();
            var paths = DesktopAppPaths.CreateForDirectory(Path.GetDirectoryName(workspace.StoragePath())!);
            using var activation = new DesktopActivationCoordinator(new AvaloniaUiDispatcher());
            using var owner = await ApplicationInstanceCoordinator.AcquireOrActivateAsync(paths, activation);
            var shutdown = new AuditShutdown();
            var window = new MainWindow(shutdown) { DataContext = root, Width = 900, Height = 650 };
            // Real native tray, but an isolated lifetime: accepted exit must not stop this audit's dispatcher.
            using var lifetime = new ClassicDesktopStyleApplicationLifetime();
            // Register Avalonia window tracking without starting/stopping a second UI main loop.
            typeof(ClassicDesktopStyleApplicationLifetime).GetMethod("BeforeInit",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(lifetime, null);
            using var tray = new DesktopTrayLifecycle(Application.Current!, lifetime, window, activation, NullLogger.Instance);
            var acceptedExits = 0;
            window.ConfigureTrayLifecycle(() => tray.IsAvailable, () => { acceptedExits++; window.Close(); });
            try
            {
                if (!tray.IsAvailable) throw new InvalidOperationException("Native tray menu is unavailable.");
                if (lifetime.ShutdownMode != ShutdownMode.OnExplicitShutdown) throw new InvalidOperationException("Implicit shutdown mode.");
                window.Show();
                activation.Attach(() => window.TryActivateExistingWindow());
                await SettleAsync(window);
                var navigation = root.Chats.Public.Navigation;
                await navigation.SelectConversationAsync(navigation.PrimaryConversations[0]);
                await SettleAsync(window);
                navigation.Draft.Text = "S3 retained draft 🐈";
                var selected = navigation.SelectedConversation;
                var history = navigation.History;
                var open = (NativeMenuItem)tray.Menu!.Items[0];
                var exit = (NativeMenuItem)tray.Menu.Items[2];
                if (open.Header != "Открыть" || exit.Header != "Выйти") throw new InvalidOperationException("Tray menu labels.");
                var list = window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "HistoryList");
                var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().Single();
                scroll.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height - 100));
                await SettleAsync(window);
                var scrollOffset = scroll.Offset.Y;
                if (scrollOffset <= 0) throw new InvalidOperationException("The scroll fixture did not produce a nonzero viewport offset.");
                var placement = root.SavedWindowPlacement;
                for (var index = 0; index < 3; index++)
                {
                    window.Close();
                    await SettleAsync(window);
                    AssertHidden();
                    open.Command!.Execute(null);
                    await UntilAsync(() => window.IsVisible && window.IsActive);
                    await SettleAsync(window);
                    AssertRetained();
                    if (Math.Abs(scroll.Offset.Y - scrollOffset) > 2) throw new InvalidOperationException("Hide/show reset scroll position.");
                    if (root.SavedWindowPlacement != placement) throw new InvalidOperationException("Hide/show reset placement.");
                }
                window.WindowState = WindowState.Maximized;
                await SettleAsync(window);
                window.WindowState = WindowState.Minimized;
                await SettleAsync(window);
                AssertHidden();
                using (var child = ApplicationInstanceActivationTests.StartSecondProcess(paths.DataDirectory))
                {
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    if (child.ExitCode != 0) throw new InvalidOperationException(await child.StandardError.ReadToEndAsync());
                }
                await SettleAsync(window);
                if (window.WindowState != WindowState.Maximized) throw new InvalidOperationException("Restore lost maximization.");
                AssertRetained();
                Console.WriteLine("S3 native: real tray/menu; close/show ×3, minimize, IPC, maximized placement, draft/history; zero connect.");

                window.Close();
                await SettleAsync(window);
                var readBefore = await workspace.Storage.ReadStates.GetAsync(workspace.A.NodeId, selected!.Id!.Value, Token);
                var incoming = await workspace.StoreAsync(workspace.A, "S3 incoming while in tray");
                workspace.Notifications.Publish(incoming);
                await UntilAsync(() => history.PendingNewMessageCount > 0 || history.Messages.Any(message => message.Body == "S3 incoming while in tray"));
                await SettleAsync(window);
                var readAfter = await workspace.Storage.ReadStates.GetAsync(workspace.A.NodeId, selected.Id!.Value, Token);
                if (readBefore.LastReadSequence != readAfter.LastReadSequence) throw new InvalidOperationException("Hidden incoming was marked read.");
                AssertHidden();
                Console.WriteLine("S3 native: committed incoming remains processed while hidden, without advancing read cursor.");
                shutdown.BeginAttempt();
                exit.Command!.Execute(null);
                exit.Command.Execute(null);
                await UntilAsync(() => shutdown.Attempts == 1);
                if (window.CanActivateExistingWindow) throw new InvalidOperationException("Exit accepted concurrent activation.");
                shutdown.Fail();
                await UntilAsync(() => window.IsVisible && window.CanActivateExistingWindow);
                if (!tray.IsAvailable || acceptedExits != 0) throw new InvalidOperationException("Failed exit disposed the tray.");
                Console.WriteLine("S3 native: hidden Exit is coalesced; persistence failure restores window and retains tray.");

                // System Quit must request durable exit even with close-to-tray selected.
                shutdown.BeginAttempt();
                if (lifetime.TryShutdown()) throw new InvalidOperationException("Quit bypassed the async barrier.");
                await UntilAsync(() => shutdown.Attempts == 2);
                shutdown.Fail();
                await UntilAsync(() => window.CanActivateExistingWindow);

                // Without a tray, close exits rather than leaving an inaccessible hidden process.
                window.ConfigureTrayLifecycle(() => false, () => { acceptedExits++; window.Close(); });
                shutdown.BeginAttempt();
                window.Close();
                await UntilAsync(() => shutdown.Attempts == 3);
                shutdown.Fail();
                await UntilAsync(() => window.CanActivateExistingWindow);

                window.WindowState = WindowState.Minimized;
                await SettleAsync(window);
                if (!window.IsVisible || history.IsWindowActive || history.FirstVisibleSequence is not null)
                    throw new InvalidOperationException("Unavailable tray hid the minimized window or retained read range.");
                await activation.RequestAsync();
                window.ConfigureTrayLifecycle(() => tray.IsAvailable, () => { acceptedExits++; window.Close(); });
                root.SelectedCloseBehavior = root.CloseBehaviorOptions.Single(option => option.Value == DesktopCloseBehavior.ExitApplication);
                shutdown.BeginAttempt();
                window.Close();
                await UntilAsync(() => shutdown.Attempts == 4);
                shutdown.Fail();
                await UntilAsync(() => window.CanActivateExistingWindow);
                root.SelectedCloseBehavior = root.CloseBehaviorOptions.Single(option => option.Value == DesktopCloseBehavior.MinimizeToTray);
                window.Close();
                shutdown.BeginAttempt();
                exit.Command!.Execute(null);
                await UntilAsync(() => shutdown.Attempts == 5);
                shutdown.Succeed();
                await UntilAsync(() => acceptedExits == 1);
                if (window.CanActivateExistingWindow) throw new InvalidOperationException("Accepted exit still activates.");
                Console.WriteLine("S3 native: Quit, unavailable-tray fallback, exit setting and successful retry use the shutdown barrier.");

                void AssertHidden()
                {
                    if (window.IsVisible || !window.CanActivateExistingWindow || shutdown.Attempts != 0 ||
                        history.IsWindowActive || history.FirstVisibleSequence is not null)
                        throw new InvalidOperationException("Hidden window remained visible/read-active or stopped application services.");
                }
                void AssertRetained()
                {
                    if (!ReferenceEquals(selected, navigation.SelectedConversation) || !ReferenceEquals(history, navigation.History) ||
                        navigation.Draft.Text != "S3 retained draft 🐈" || workspace.Supervisor.ConnectCalls != 0)
                        throw new InvalidOperationException("Tray return recreated workspace or initiated connection.");
                }
            }
            finally
            {
                if (window.CanActivateExistingWindow)
                {
                    shutdown.BeginAttempt(); window.RequestExit();
                    await UntilAsync(() => shutdown.Attempts > 0);
                    shutdown.Succeed(); await SettleAsync(window);
                }
            }
        }

        private sealed class AuditShutdown : IDesktopShutdownCoordinator
        {
            private TaskCompletionSource _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool IsCompleted { get; private set; }
            public int Attempts { get; private set; }
            public void BeginAttempt() => _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task ShutdownAsync(CancellationToken cancellationToken = default)
            {
                Attempts++;
                return _pending.Task.WaitAsync(cancellationToken);
            }
            public void Fail() => _pending.TrySetException(new DesktopShutdownException("Synthetic failure", new IOException()));
            public void Succeed() { IsCompleted = true; _pending.TrySetResult(); }
        }
    }
}
