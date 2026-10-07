using MeshCoreMessenger.Desktop.Notifications;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreMessenger.Desktop.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    public static class NativeNotificationIntegrationAudit
    {
        public static async Task RunAsync()
        {
            await using var native = NotificationPlatformFactory.Create();
            await native.InitializeAsync(Token);
            if (native.Status == NotificationPlatformStatus.Unavailable)
                throw new InvalidOperationException("Native notification bridge is unavailable.");
            Console.WriteLine($"Native notification platform: {native.Status}; no permission dialog requested.");
            await using var workspace = await Workspace.CreateAsync(publicMessageCount: 40);
            await workspace.Root.StopAsync();
            var dispatcher = new AvaloniaUiDispatcher();
            var root = workspace.CreateRoot(dispatcher: dispatcher); workspace.Root = root;
            await root.LoadAsync();
            var window = new MainWindow { DataContext = root, Width = 900, Height = 650 };
            using var activation = new DesktopActivationCoordinator(dispatcher);
            var router = new NotificationNavigationRouter(root, workspace.Storage.ConversationDirectory, workspace.Storage.History);
            activation.Attach(window.TryActivateExistingWindow); activation.AttachNavigation(router.OpenAsync);
            var folder = Path.Combine(Path.GetTempPath(), "mcm-native-click-" + Guid.NewGuid().ToString("N"));
            var paths = DesktopAppPaths.CreateForDirectory(folder);
            var registry = new NotificationTargetRegistry(Path.Combine(folder, "targets"), TimeProvider.System);
            var platform = new ClickPlatform();
            await using var clicks = new NotificationClickController(platform, registry, paths, activation, dispatcher, NullLogger<NotificationClickController>.Instance);
            try
            {
                window.Show(); await SettleAsync(window); window.Hide();
                var message = await workspace.StoreAsync(workspace.A, "S6 click target 🐈");
                var token = registry.Add(folder, new MessageNotificationTarget(workspace.A.NodeId, workspace.A.ConversationId, message.MessageId));
                clicks.Start(); await Task.Run(() => platform.Click(token));
                await UntilAsync(() => window.IsVisible && root.Messages.Any(item => item.Id == message.MessageId));
                await SettleAsync(window);
                if (root.SelectedConversation?.Id != workspace.A.ConversationId || workspace.Supervisor.ConnectCalls != 0)
                    throw new InvalidOperationException("Notification activation changed connection or lost scope.");
                var stale = registry.Add(folder, new MessageNotificationTarget(workspace.A.NodeId, workspace.A.ConversationId, Guid.NewGuid()));
                await Task.Run(() => platform.Click(stale));
                await UntilAsync(() => root.Status.Contains("недоступно", StringComparison.Ordinal));
                Console.WriteLine("S6 native UI: background click restores hidden window, opens exact SQLite message; stale fallback; zero connect.");
            }
            finally { await clicks.StopAsync(); window.Close(); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
        private sealed class ClickPlatform : IDesktopNotificationPlatform
        {
            public NotificationPlatformStatus Status => NotificationPlatformStatus.Enabled;
            public event EventHandler? StatusChanged { add { } remove { } }
            public event EventHandler<string>? Activated;
            public void Click(string token) => Activated?.Invoke(this, token);
            public Task InitializeAsync(CancellationToken token = default) => Task.CompletedTask;
            public Task RequestPermissionAsync(CancellationToken token) => Task.CompletedTask;
            public Task<string?> GetStartupTokenAsync(string[] args, CancellationToken token = default) => Task.FromResult<string?>(null);
            public Task ShowAsync(NotificationRequest request, CancellationToken token) => Task.CompletedTask;
            public Task RemoveAsync(string identifier) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
