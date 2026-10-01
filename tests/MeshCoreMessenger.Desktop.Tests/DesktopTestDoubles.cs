using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
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

internal sealed class ImmediateSearchDelay : ISearchDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

internal sealed class ImmediateDraftDelay : IDraftDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

internal sealed class ControlledDraftDelay : IDraftDelay
{
    private readonly object _gate = new();
    private readonly List<DelayRequest> _requests = [];

    public IReadOnlyList<DelayRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var request = new DelayRequest(delay, cancellationToken);
            _requests.Add(request);
            return request.Completion.Task.WaitAsync(cancellationToken);
        }
    }

    internal sealed record DelayRequest(TimeSpan Delay, CancellationToken CancellationToken)
    {
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed class FakeDraftBuffer : IDraftBuffer
{
    private readonly Dictionary<string, string> _texts = [];

    public List<(DraftTarget Target, string Text, long Revision)> Updates { get; } = [];
    public List<DraftTarget> Flushes { get; } = [];
    public Exception? FlushFailure { get; set; }
    public TaskCompletionSource? FlushGate { get; set; }

    public Task<string> LoadTextAsync(
        DraftTarget target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_texts.GetValueOrDefault(Key(target)) ?? string.Empty);
    }

    public void Update(DraftTarget target, string text, long revision)
    {
        Updates.Add((target, text, revision));
        _texts[Key(target)] = text;
    }

    public async Task FlushAsync(
        DraftTarget target,
        CancellationToken cancellationToken = default)
    {
        Flushes.Add(target);
        if (FlushGate is not null)
        {
            await FlushGate.Task.WaitAsync(cancellationToken);
        }
        if (FlushFailure is { } failure)
        {
            throw failure;
        }
    }

    public void Seed(DraftTarget target, string text) => _texts[Key(target)] = text;

    private static string Key(DraftTarget target) =>
        $"{target.NodeId:D}:{(int)target.Kind}:{Convert.ToHexString(target.Identity)}";
}

internal sealed class ControlledSearchDelay : ISearchDelay
{
    private readonly object _gate = new();
    private readonly List<DelayRequest> _requests = [];

    public IReadOnlyList<DelayRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var request = new DelayRequest(delay, cancellationToken);
            _requests.Add(request);
            return request.Completion.Task.WaitAsync(cancellationToken);
        }
    }

    internal sealed record DelayRequest(TimeSpan Delay, CancellationToken CancellationToken)
    {
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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

internal sealed class FakeConversationReadStateService
    : IConversationReadStateStore, IDurableReadStateWrites
{
    private readonly Dictionary<(Guid NodeId, Guid ConversationId), ConversationReadState> _states = [];

    public List<HistoryMessagePosition> Advances { get; } = [];
    public Func<HistoryMessagePosition, ConversationReadState>? AdvanceResult { get; set; }
    public Exception? AdvanceFailure { get; set; }
    public TaskCompletionSource? AdvanceGate { get; set; }
    public bool IsPaused => AdvanceFailure is not null;
    public int PendingCount => 0;

    public void Set(ConversationReadState state) =>
        _states[(state.NodeId, state.ConversationId)] = state;

    public Task<ConversationReadState> GetAsync(
        Guid nodeId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_states.GetValueOrDefault((nodeId, conversationId)) ??
            new ConversationReadState(nodeId, conversationId, 0, 0, null));
    }

    public async Task<ConversationReadState> AdvanceAsync(
        HistoryMessagePosition through,
        CancellationToken cancellationToken = default)
    {
        Advances.Add(through);
        if (AdvanceGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (AdvanceFailure is { } failure)
        {
            throw failure;
        }

        var state = AdvanceResult?.Invoke(through) ??
            new ConversationReadState(
                through.NodeId,
                through.ConversationId,
                through.LocalSequence,
                0,
                null);
        Set(state);
        return state;
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task RetryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AdvanceFailure = null;
        return Task.CompletedTask;
    }
}
