using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreMessenger.Desktop.Views;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    public static class NativeInstanceActivationAudit
    {
        public static async Task RunAsync()
        {
            await using var workspace = await Workspace.CreateAsync();
            await workspace.Root.StopAsync();
            var root = workspace.CreateRoot(dispatcher: new AvaloniaUiDispatcher());
            workspace.Root = root;
            await root.LoadAsync();
            var paths = DesktopAppPaths.CreateForDirectory(Path.GetDirectoryName(workspace.StoragePath())!);
            using var activation = new DesktopActivationCoordinator(new AvaloniaUiDispatcher());
            using var owner = await ApplicationInstanceCoordinator.AcquireOrActivateAsync(paths, activation);
            var shutdown = new RetryableAuditShutdown();
            var window = new MainWindow(shutdown) { DataContext = root, Width = 900, Height = 650 };
            try
            {
                window.Show();
                activation.Attach(() =>
                {
                    _ = Application.Current?.TryGetFeature<IActivatableLifetime>()?.TryLeaveBackground();
                    return window.TryActivateExistingWindow();
                });
                await SettleAsync(window);
                var navigation = root.Chats.Public.Navigation;
                await navigation.SelectConversationAsync(navigation.PrimaryConversations[0]);
                navigation.Draft.Text = "S1 native draft 🐈";
                var selected = navigation.SelectedConversation;
                var history = navigation.History;
                var placement = root.SavedWindowPlacement;
                foreach (var state in new[] { "normal", "minimized", "hidden", "maximized-minimized" })
                {
                    if (state == "hidden") window.Hide();
                    else if (state == "maximized-minimized")
                    {
                        window.WindowState = WindowState.Maximized;
                        await SettleAsync(window);
                        window.WindowState = WindowState.Minimized;
                    }
                    else if (state == "minimized") window.WindowState = WindowState.Minimized;
                    await SettleAsync(window);
                    using var child = ApplicationInstanceActivationTests.StartSecondProcess(paths.DataDirectory);
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    var error = await child.StandardError.ReadToEndAsync();
                    await SettleAsync(window);
                    if (child.ExitCode != 0 || !window.IsVisible || window.WindowState == WindowState.Minimized || !window.IsActive ||
                        !ReferenceEquals(selected, navigation.SelectedConversation) || !ReferenceEquals(history, navigation.History) ||
                        navigation.Draft.Text != "S1 native draft 🐈" || workspace.Supervisor.ConnectCalls != 0)
                        throw new InvalidOperationException($"S1 native {state} failed: exit={child.ExitCode}, visible={window.IsVisible}, " +
                            $"state={window.WindowState}, active={window.IsActive}; {error}");
                    if (state == "hidden" && root.SavedWindowPlacement != placement)
                        throw new InvalidOperationException("Showing a hidden window reset placement.");
                    if (state == "maximized-minimized" && window.WindowState != WindowState.Maximized)
                        throw new InvalidOperationException("Maximized state was lost during activation.");
                    Console.WriteLine($"S1 native {state}: exit=0, visible, active, state={window.WindowState}; selection/history/draft retained; zero connect.");
                }
                window.Close();
                if (window.CanActivateExistingWindow) throw new InvalidOperationException("A closing window accepted activation.");
                using (var closingClient = ApplicationInstanceActivationTests.StartSecondProcess(paths.DataDirectory))
                {
                    await closingClient.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    if (closingClient.ExitCode != 2) throw new InvalidOperationException("Closing window did not reject IPC activation.");
                }
                shutdown.FailAttempt();
                await SettleAsync(window);
                using (var recoveredClient = ApplicationInstanceActivationTests.StartSecondProcess(paths.DataDirectory))
                {
                    await recoveredClient.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    if (recoveredClient.ExitCode != 0 || !window.IsActive)
                        throw new InvalidOperationException("Failed shutdown did not allow activation again.");
                }
                Console.WriteLine("S1 native closing: exit=2; after failed shutdown: exit=0, existing window active.");
            }
            finally { shutdown.AllowExit(); window.Close(); }
            if (window.TryActivateExistingWindow()) throw new InvalidOperationException("A closed window accepted activation.");
        }

        private sealed class RetryableAuditShutdown : IDesktopShutdownCoordinator
        {
            private readonly TaskCompletionSource _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool IsCompleted { get; private set; }
            public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
                IsCompleted ? Task.CompletedTask : _pending.Task.WaitAsync(cancellationToken);
            public void FailAttempt() => _pending.TrySetException(new DesktopShutdownException("Synthetic persistence failure", new IOException()));
            public void AllowExit() => IsCompleted = true;
        }
    }
}
