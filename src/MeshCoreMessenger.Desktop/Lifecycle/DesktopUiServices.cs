using Avalonia.Threading;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Desktop.Lifecycle;

public interface IUiDispatcher
{
    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}

internal sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public async Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await Dispatcher.UIThread.InvokeAsync(
            action,
            DispatcherPriority.Normal,
            cancellationToken);
    }
}

public interface IMessageCommitNotifications
{
    event EventHandler<IncomingMessageCommitEvent>? MessageCommitted;
}

internal sealed class MessageCommitNotifications(MessageIngestor ingestor) : IMessageCommitNotifications
{
    public event EventHandler<IncomingMessageCommitEvent>? MessageCommitted
    {
        add => ingestor.MessageCommitted += value;
        remove => ingestor.MessageCommitted -= value;
    }
}
