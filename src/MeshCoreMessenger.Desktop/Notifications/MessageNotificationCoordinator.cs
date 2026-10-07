using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.Lifecycle;

namespace MeshCoreMessenger.Desktop.Notifications;

/// <summary>Message-specific transient grouping; synchronous ingress observers never perform reads or await delivery.</summary>
internal sealed class MessageNotificationCoordinator : IAsyncDisposable
{
    internal const int Capacity = 128;
    private readonly object _gate = new();
    private readonly IMessageCommitNotifications _commits;
    private readonly IDesktopNotificationService _notifications;
    private readonly TimeProvider _time;
    private readonly TimeSpan _window;
    private readonly Dictionary<(Guid Node, Guid Conversation, IncomingSynchronization? Batch), MessageNotificationGroup> _pending = [];
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly MessageNotificationGroup?[] _overflow = new MessageNotificationGroup?[2];
    private readonly HashSet<IncomingSynchronization> _observedBatches = [];
    private bool _started;
    private bool _stopped;
    private Task _worker = Task.CompletedTask;

    public MessageNotificationCoordinator(IMessageCommitNotifications commits, IDesktopNotificationService notifications,
        TimeProvider time, TimeSpan? coalescingWindow = null)
    {
        _commits = commits; _notifications = notifications; _time = time;
        _window = coalescingWindow ?? TimeSpan.FromMilliseconds(500);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _stopped) return;
            _started = true; _commits.MessageCommitted += OnCommitted;
            _worker = Task.Run(RunAsync);
        }
    }

    private void OnCommitted(object? sender, IncomingMessageCommitEvent commit)
    {
        if (!commit.Message.Inserted) return;
        lock (_gate)
        {
            if (_stopped) return;
            var id = commit.Message;
            var key = (id.NodeId, id.ConversationId, commit.InitialSynchronization);
            if (_pending.TryGetValue(key, out var pending))
                _pending[key] = pending with { Latest = commit, FirstSequence = Math.Min(pending.FirstSequence, id.LocalSequence), Count = pending.Count + 1 };
            else if (_pending.Count < Capacity)
            {
                _pending.Add(key, new(commit, id.LocalSequence, 1));
                ObserveCompletion(commit.InitialSynchronization);
            }
            else
            {
                var index = commit.Category == IncomingMessageCategory.Private ? 0 : 1;
                var count = _overflow[index] is { } previous &&
                    ReferenceEquals(previous.Latest.InitialSynchronization, commit.InitialSynchronization) &&
                    previous.Latest.Message.NodeId == id.NodeId ? previous.Count + 1 : 1;
                _overflow[index] = new(commit, id.LocalSequence, count, IsOverflow: true);
                if (count == 1) ObserveCompletion(commit.InitialSynchronization);
            }
            Wake();
        }
    }

    private void ObserveCompletion(IncomingSynchronization? batch)
    {
        if (batch is null || batch.Completion.IsCompleted || _observedBatches.Count >= Capacity || !_observedBatches.Add(batch)) return;
        _ = batch.Completion.ContinueWith(_ =>
        {
            lock (_gate) { _observedBatches.Remove(batch); if (!_stopped) Wake(); }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void Wake() { if (_signal.CurrentCount == 0) _signal.Release(); }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_stop.Token).ConfigureAwait(false);
                await Task.Delay(_window, _time, _stop.Token).ConfigureAwait(false);
                List<MessageNotificationGroup> ready = [];
                List<MessageNotificationGroup> overflow = [];
                lock (_gate)
                {
                    if (_stopped) return;
                    foreach (var entry in _pending.ToArray())
                    {
                        var completion = entry.Key.Batch?.Completion;
                        if (completion is not null && !completion.IsCompleted) continue;
                        _pending.Remove(entry.Key);
                        if (completion is null || completion.Result) ready.Add(entry.Value);
                    }
                    for (var index = 0; index < _overflow.Length; index++)
                    {
                        if (_overflow[index] is not { } extra || !(extra.Latest.InitialSynchronization?.Completion.IsCompleted ?? true)) continue;
                        _overflow[index] = null;
                        if (extra.Latest.InitialSynchronization?.Completion.Result ?? true) overflow.Add(extra);
                    }
                }
                foreach (var initial in ready.Where(group => group.Latest.InitialSynchronization is not null)
                    .GroupBy(group => group.Latest.InitialSynchronization!))
                {
                    var extra = overflow.Where(group => ReferenceEquals(group.Latest.InitialSynchronization, initial.Key)).ToArray();
                    _notifications.Submit(new MessageNotificationRequest([.. initial, .. extra], true));
                    foreach (var group in extra) overflow.Remove(group);
                }
                var liveGroups = ready.Where(group => group.Latest.InitialSynchronization is null).ToArray();
                var liveOverflow = overflow.Where(group => group.Latest.InitialSynchronization is null).ToArray();
                if (liveOverflow.Length > 0)
                {
                    // Collapse the entire overloaded live burst, rather than emit Capacity individual banners.
                    _notifications.Submit(new MessageNotificationRequest([.. liveGroups, .. liveOverflow], true));
                    foreach (var group in liveOverflow) overflow.Remove(group);
                }
                else foreach (var live in liveGroups)
                    _notifications.Submit(new MessageNotificationRequest([live], false));
                foreach (var remaining in overflow.GroupBy(group => group.Latest.InitialSynchronization))
                    _notifications.Submit(new MessageNotificationRequest(remaining.ToArray(), true));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (!_stopped)
            {
                _stopped = true; _commits.MessageCommitted -= OnCommitted;
                _pending.Clear(); Array.Clear(_overflow); _observedBatches.Clear(); _stop.Cancel();
            }
            return _worker;
        }
    }

    private int _disposed;
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref _disposed, 1) == 0) { _stop.Dispose(); _signal.Dispose(); }
    }
}
