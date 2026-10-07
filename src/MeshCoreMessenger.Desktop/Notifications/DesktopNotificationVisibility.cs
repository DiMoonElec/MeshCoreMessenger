using Avalonia.Controls;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreMessenger.Desktop.Views;

namespace MeshCoreMessenger.Desktop.Notifications;

internal sealed class DesktopNotificationVisibility(IUiDispatcher dispatcher) : IMessageNotificationVisibility
{
    private MainWindow? _window;
    private MainWindowViewModel? _root;
    public void Attach(MainWindow window, MainWindowViewModel root) { _window = window; _root = root; }

    public async Task<bool> IsVisibleAsync(MessageNotificationGroup group, CancellationToken cancellationToken)
    {
        var visible = false;
        await dispatcher.InvokeAsync(() =>
        {
            if (_window is not { IsVisible: true, IsActive: true } window || window.WindowState == WindowState.Minimized ||
                _root is not { IsChatWorkspaceVisible: true } root || root.Modal.IsOpen) return;
            var message = group.Latest.Message;
            var history = root.Navigation.History;
            visible = root.ViewedNode?.Id == message.NodeId && root.SelectedConversation?.Id == message.ConversationId &&
                history.IsWindowActive && history.FirstVisibleSequence is { } first && first <= group.FirstSequence &&
                history.LastVisibleSequence is { } last && last >= message.LocalSequence &&
                history.Messages.Any(item => item.Id == message.MessageId);
        }, cancellationToken).ConfigureAwait(false);
        return visible;
    }
}

internal sealed class NotificationDesktopUiLifetime(MainWindowViewModel root, MessageNotificationCoordinator messages,
    DesktopNotificationService notifications) : IDesktopUiLifetime
{
    public async Task StopAsync()
    {
        // Start cancellation of delivery immediately, then quiesce the existing durable UI owners.
        var delivery = notifications.StopAsync();
        await messages.StopAsync().ConfigureAwait(false);
        await delivery.ConfigureAwait(false);
        await root.StopAsync().ConfigureAwait(false);
    }
    public Task ReportShutdownFailureAsync(Exception exception) => ((IDesktopUiLifetime)root).ReportShutdownFailureAsync(exception);
}
