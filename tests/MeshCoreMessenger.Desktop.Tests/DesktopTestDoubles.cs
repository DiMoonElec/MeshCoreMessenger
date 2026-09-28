using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.Lifecycle;

namespace MeshCoreMessenger.Desktop.Tests;

internal sealed class FakeConnectionSupervisor : IConnectionSupervisor
{
    public ConnectionSupervisorSnapshot Snapshot { get; private set; } = new(
        ConnectionSupervisorState.Offline,
        0,
        null,
        null,
        null,
        null,
        null);

    public event EventHandler<ConnectionSupervisorStateChangedEventArgs>? StateChanged;

    public int ConnectCalls { get; private set; }
    public int DisconnectCalls { get; private set; }
    public List<Guid> SwitchedProfiles { get; } = [];

    public Task StartAutoConnectAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ConnectNowAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectCalls++;
        return Task.CompletedTask;
    }

    public Task SwitchProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SwitchedProfiles.Add(profileId);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DisconnectCalls++;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Publish(ConnectionSupervisorSnapshot snapshot)
    {
        var previous = Snapshot;
        Snapshot = snapshot;
        StateChanged?.Invoke(this, new ConnectionSupervisorStateChangedEventArgs(previous, snapshot));
    }
}

internal sealed class FakeMessageCommitNotifications : IMessageCommitNotifications
{
    public event EventHandler<IncomingMessageCommitEvent>? MessageCommitted;

    public void Publish(StoredIncomingMessage message) =>
        MessageCommitted?.Invoke(this, new IncomingMessageCommitEvent(message));
}

internal sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public int Calls { get; private set; }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        action();
        return Task.CompletedTask;
    }
}

internal sealed class QueuedUiDispatcher : IUiDispatcher
{
    private readonly Queue<(Action Action, TaskCompletionSource Completion, CancellationToken Cancellation)> _queue = [];

    public int PendingCount
    {
        get
        {
            lock (_queue)
            {
                return _queue.Count;
            }
        }
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_queue)
        {
            _queue.Enqueue((action, completion, cancellationToken));
        }

        return completion.Task;
    }

    public void RunNext()
    {
        (Action Action, TaskCompletionSource Completion, CancellationToken Cancellation) item;
        lock (_queue)
        {
            item = _queue.Dequeue();
        }

        if (item.Cancellation.IsCancellationRequested)
        {
            item.Completion.SetCanceled(item.Cancellation);
            return;
        }

        try
        {
            item.Action();
            item.Completion.SetResult();
        }
        catch (Exception exception)
        {
            item.Completion.SetException(exception);
        }
    }
}
