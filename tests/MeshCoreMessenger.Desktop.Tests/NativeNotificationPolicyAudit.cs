using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Notifications;
using MeshCoreMessenger.Desktop.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    public static class NativeNotificationPolicyAudit
    {
        public static async Task RunAsync()
        {
            await using var workspace = await Workspace.CreateAsync(publicMessageCount: 40, variableMessageHeight: true);
            await workspace.Root.StopAsync();
            var dispatcher = new AvaloniaUiDispatcher();
            var root = workspace.CreateRoot(dispatcher: dispatcher); workspace.Root = root;
            await root.LoadAsync();
            var window = new MainWindow { DataContext = root, Width = 900, Height = 650 };
            var visibility = new DesktopNotificationVisibility(dispatcher); visibility.Attach(window, root);
            var adapter = new CaptureAdapter();
            var policy = new MessageNotificationPolicy(workspace.Preferences, workspace.Storage.MessageDetails, visibility,
                NullLogger<MessageNotificationPolicy>.Instance);
            await using var service = new DesktopNotificationService(adapter, [policy], NullLogger<DesktopNotificationService>.Instance);
            var other = new Window { Width = 300, Height = 200 };
            try
            {
                window.Show(); window.Activate(); await SettleAsync(window);
                var navigation = root.Chats.Public.Navigation;
                await navigation.SelectConversationAsync(navigation.PrimaryConversations[0]);
                await navigation.History.JumpToLatestAsync(); await SettleAsync(window);
                var message = navigation.History.Messages.Last();
                var group = new MessageNotificationGroup(new(new(message.Id, Guid.NewGuid(), workspace.A.NodeId,
                    workspace.A.ConversationId, message.LocalSequence, true)) { Category = IncomingMessageCategory.Channel }, message.LocalSequence, 1);
                if (!await visibility.IsVisibleAsync(group, Token)) throw new InvalidOperationException("Native visible fixture was not actually visible.");
                service.Submit(new MessageNotificationRequest([group], false)); service.Submit(new("sentinel", ""));
                if ((await adapter.NextAsync()).Title != "sentinel") throw new InvalidOperationException("Actually visible message notified.");

                window.Hide(); await SettleAsync(window);
                var before = await workspace.Storage.ReadStates.GetAsync(workspace.A.NodeId, workspace.A.ConversationId, Token);
                var inserted = await workspace.StoreAsync(workspace.A, "S5 hidden incoming 🐈");
                workspace.Notifications.Publish(inserted);
                await UntilAsync(() => navigation.History.PendingNewMessageCount > 0);
                group = new(new(inserted) { Category = IncomingMessageCategory.Channel }, inserted.LocalSequence, 1);
                service.Submit(new MessageNotificationRequest([group], false));
                var hidden = await adapter.NextAsync();
                if (hidden.Title != "Shared name" || hidden.Body != "S5 hidden incoming 🐈" ||
                    hidden.Target is not MessageNotificationTarget target || target.NodeId != workspace.A.NodeId ||
                    target.ConversationId != workspace.A.ConversationId || target.MessageId != inserted.MessageId)
                    throw new InvalidOperationException("Exact persisted notification projection/target failed.");
                var after = await workspace.Storage.ReadStates.GetAsync(workspace.A.NodeId, workspace.A.ConversationId, Token);
                if (before.LastReadSequence != after.LastReadSequence) throw new InvalidOperationException("Notification advanced unread cursor.");

                window.TryActivateExistingWindow(); await SettleAsync(window);
                await navigation.History.JumpToLatestAsync(); await SettleAsync(window);
                other.Show(); other.Activate(); await SettleAsync(other);
                service.Submit(new MessageNotificationRequest([group], false));
                if ((await adapter.NextAsync()).Target is not MessageNotificationTarget) throw new InvalidOperationException("Inactive window suppressed banner.");
                other.Hide(); window.Activate(); await SettleAsync(window);
                var list = window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "HistoryList");
                list.GetVisualDescendants().OfType<ScrollViewer>().Single().Offset = default;
                await SettleAsync(window);
                if (await visibility.IsVisibleAsync(group, Token)) throw new InvalidOperationException("Scrolled-up fixture still sees latest message.");
                service.Submit(new MessageNotificationRequest([group], false)); await adapter.NextAsync();
                window.WindowState = WindowState.Minimized; await SettleAsync(window);
                service.Submit(new MessageNotificationRequest([group], false)); await adapter.NextAsync();
                if (workspace.Supervisor.ConnectCalls != 0) throw new InvalidOperationException("Notifications connected the node.");
                Console.WriteLine("S5 native: active/actually-visible suppressed; hidden/inactive/scrolled/minimized notify; real SQLite title/preview/IDs; unread unchanged; zero connect.");
            }
            finally { other.Close(); window.Close(); await service.StopAsync(); }
        }

        private sealed class CaptureAdapter : IDesktopNotificationAdapter
        {
            private readonly Channel<NotificationRequest> _requests = Channel.CreateUnbounded<NotificationRequest>();
            public Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken)
            { cancellationToken.ThrowIfCancellationRequested(); _requests.Writer.TryWrite(request); return Task.CompletedTask; }
            public async Task<NotificationRequest> NextAsync() => await _requests.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
    }
}
