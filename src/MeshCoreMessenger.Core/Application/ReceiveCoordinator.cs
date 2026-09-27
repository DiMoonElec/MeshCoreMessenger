using System.Threading.Channels;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;

namespace MeshCoreMessenger.Core.Application;

/// <summary>Owns the one-at-a-time Companion message drain for an identified session.</summary>
public sealed class ReceiveCoordinator : IAsyncDisposable
{
    private readonly DirectoryService _directories;
    private readonly IDirectoryStore _directoryStore;
    private readonly MessageIngestor _ingestor;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _drainSignal = new(0);
    private readonly SemaphoreSlim _retrySignal = new(0);
    private readonly List<CompanionSessionEvent> _messagesBeforeDirectories = [];
    private readonly Dictionary<byte, ChannelBindingRecord> _stableBindings = [];
    private readonly HashSet<byte> _transitionSlots = [];
    private CancellationTokenSource? _stop;
    private Task? _eventPump;
    private Task? _worker;
    private TaskCompletionSource _initial = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<Exception> _ingestFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CompanionSession? _session;
    private Guid _nodeId;
    private int _directoriesReady;
    private int _initialSucceeded;
    private int _drainPending;
    private int _disposed;
    private volatile ReceiveCoordinatorState _state = ReceiveCoordinatorState.Created;

    public ReceiveCoordinator(DirectoryService directories, IDirectoryStore directoryStore, MessageIngestor ingestor)
    {
        _directories = directories;
        _directoryStore = directoryStore;
        _ingestor = ingestor;
        _ingestor.Failed += OnIngestFailed;
    }

    public ReceiveCoordinatorState State => _state;
    public Exception? LastError { get; private set; }

    /// <summary>Completes after directories, initial drain, both event barriers and binding activation.</summary>
    public Task SynchronizeAsync(CompanionSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ThrowIfDisposed();
        Start(session);
        return _initial.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Resumes a paused durable ingress after its storage problem has been corrected.</summary>
    public Task RetryAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (State != ReceiveCoordinatorState.NeedsAttention || LastError is ReceiveDrainTimeoutException)
        {
            throw new InvalidOperationException("The receive coordinator is not waiting for an ingest retry.");
        }

        return RetryCoreAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var stop = Interlocked.Exchange(ref _stop, null);
        if (stop is null)
        {
            return;
        }

        stop.Cancel();
        _retrySignal.Release();
        _drainSignal.Release();
        var tasks = new[] { _eventPump, _worker }.Where(task => task is not null).Cast<Task>();
        await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        stop.Dispose();
        _state = ReceiveCoordinatorState.Stopped;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ingestor.Failed -= OnIngestFailed;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _drainSignal.Dispose();
        _retrySignal.Dispose();
    }

    private void Start(CompanionSession session)
    {
        lock (_gate)
        {
            if (_session is not null)
            {
                if (!ReferenceEquals(_session, session))
                {
                    throw new InvalidOperationException("A receive coordinator belongs to one Companion session.");
                }

                return;
            }

            if (session.State != CompanionSessionState.Identified || session.LocalNodeId is not { } nodeId)
            {
                throw new InvalidOperationException("Receive synchronization requires an identified Companion session.");
            }

            _session = session;
            _nodeId = nodeId;
            _stop = new CancellationTokenSource();
            _state = ReceiveCoordinatorState.Synchronizing;
            _eventPump = Task.Run(() => PumpEventsAsync(session, _stop.Token));
            _worker = Task.Run(() => RunAsync(session, _stop.Token));
        }
    }

    private async Task RetryCoreAsync(CancellationToken cancellationToken)
    {
        _ingestFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _ingestor.RetryAsync(cancellationToken).ConfigureAwait(false);
        LastError = null;
        _retrySignal.Release();
    }

    private async Task RunAsync(CompanionSession session, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _directories.SynchronizeAsync(session, cancellationToken).ConfigureAwait(false);
            InstallInitialBindings(snapshot);
            await EnqueueMessagesReceivedBeforeDirectoriesAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteWithIngestRetryAsync(
                () => DrainOnePassAsync(session, cancellationToken), cancellationToken).ConfigureAwait(false);
            await _directories.CommitPendingChannelTransitionsAsync(
                snapshot, cancellationToken).ConfigureAwait(false);
            await ActivateTransitionBindingsAsync(snapshot, cancellationToken).ConfigureAwait(false);
            if (ConsumeDrainPending())
            {
                await ExecuteWithIngestRetryAsync(
                    () => DrainUntilQuiescentAsync(session, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            _state = ReceiveCoordinatorState.Online;
            Volatile.Write(ref _initialSucceeded, 1);
            _initial.TrySetResult();
            await MonitorSignalsAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            var timeout = new ReceiveDrainTimeoutException(
                "SYNC_NEXT_MESSAGE timed out. This Companion session must be replaced before another drain.", exception);
            LastError = timeout;
            _state = ReceiveCoordinatorState.NeedsAttention;
            _initial.TrySetException(timeout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _initial.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            LastError = exception;
            _state = ReceiveCoordinatorState.NeedsAttention;
            _initial.TrySetException(exception);
        }
    }

    private async Task WaitForRetryAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _retrySignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await FlushIngestOrThrowAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (IngestPausedException exception)
            {
                LastError = exception.InnerException ?? exception;
                _state = ReceiveCoordinatorState.NeedsAttention;
            }
        }
    }

    private async Task MonitorSignalsAsync(CompanionSession session, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _drainSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!ConsumeDrainPending() || _ingestor.IsPaused)
            {
                continue;
            }

            await ExecuteWithIngestRetryAsync(
                () => DrainUntilQuiescentAsync(session, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteWithIngestRetryAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await operation().ConfigureAwait(false);
                return;
            }
            catch (IngestPausedException exception)
            {
                LastError = exception.InnerException ?? exception;
                _state = ReceiveCoordinatorState.NeedsAttention;
                _initial.TrySetException(exception);
                await WaitForRetryAsync(cancellationToken).ConfigureAwait(false);
                _state = Volatile.Read(ref _initialSucceeded) != 0
                    ? ReceiveCoordinatorState.Online
                    : ReceiveCoordinatorState.Synchronizing;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task PumpEventsAsync(CompanionSession session, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in session.Events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (item.Kind)
                {
                    case CompanionSessionEventKind.MessageReceived when item.Message is not null:
                        await HandleMessageAsync(item, cancellationToken).ConfigureAwait(false);
                        break;
                    case CompanionSessionEventKind.PushPacketReceived when item.RawPacketType == (byte)PacketType.MessagesWaiting:
                        RequestDrain();
                        break;
                    case CompanionSessionEventKind.EventBarrier:
                        item.BarrierCompletion?.TrySetResult();
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleMessageAsync(CompanionSessionEvent item, CancellationToken cancellationToken)
    {
        ChannelBindingRecord? binding = null;
        byte[]? unknownIdentity = null;
        lock (_gate)
        {
            if (Volatile.Read(ref _directoriesReady) == 0)
            {
                _messagesBeforeDirectories.Add(item);
                return;
            }

            ResolveChannel(item.Message!, out binding, out unknownIdentity);
        }

        await EnqueueAsync(item, binding, unknownIdentity, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnqueueMessagesReceivedBeforeDirectoriesAsync(CancellationToken cancellationToken)
    {
        CompanionSessionEvent[] pending;
        lock (_gate)
        {
            pending = _messagesBeforeDirectories.ToArray();
            _messagesBeforeDirectories.Clear();
        }

        foreach (var item in pending)
        {
            ChannelBindingRecord? binding;
            byte[]? unknownIdentity;
            lock (_gate)
            {
                ResolveChannel(item.Message!, out binding, out unknownIdentity);
            }
            await EnqueueAsync(item, binding, unknownIdentity, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task EnqueueAsync(CompanionSessionEvent item, ChannelBindingRecord? binding, byte[]? unknownIdentity, CancellationToken cancellationToken) =>
        _ingestor.EnqueueAsync(new IncomingMessageEnvelope(
            Guid.NewGuid(), item.SessionId, _nodeId, item.Message!, item.OccurredUtc, binding, unknownIdentity), cancellationToken);

    private async Task DrainUntilQuiescentAsync(CompanionSession session, CancellationToken cancellationToken)
    {
        do
        {
            ConsumeDrainPending(); // The pass itself covers all earlier signals.
            await DrainOnePassAsync(session, cancellationToken).ConfigureAwait(false);
        }
        while (Volatile.Read(ref _drainPending) != 0);
    }

    private async Task DrainOnePassAsync(CompanionSession session, CancellationToken cancellationToken)
    {
        await session.DrainMessagesAsync(cancellationToken).ConfigureAwait(false);
        await session.FlushEventsForConsumerAsync(cancellationToken).ConfigureAwait(false);
        await FlushIngestOrThrowAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushIngestOrThrowAsync(CancellationToken cancellationToken)
    {
        if (_ingestor.IsPaused)
        {
            throw new IngestPausedException(LastError);
        }

        var flush = _ingestor.FlushAsync(cancellationToken);
        var completed = await Task.WhenAny(flush, _ingestFailure.Task).ConfigureAwait(false);
        if (completed != flush)
        {
            throw new IngestPausedException(await _ingestFailure.Task.ConfigureAwait(false));
        }

        await flush.ConfigureAwait(false);
        if (_ingestor.IsPaused)
        {
            throw new IngestPausedException(LastError);
        }
    }

    private void InstallInitialBindings(DirectorySnapshotResult snapshot)
    {
        lock (_gate)
        {
            _transitionSlots.Clear();
            foreach (var transition in snapshot.PendingChannelTransitions)
            {
                _transitionSlots.Add(transition.Slot);
            }
            _stableBindings.Clear();
            foreach (var binding in snapshot.ActiveBindings.Where(binding => !_transitionSlots.Contains(binding.Slot)))
            {
                _stableBindings.Add(binding.Slot, binding);
            }
            Volatile.Write(ref _directoriesReady, 1);
        }
    }

    private async Task ActivateTransitionBindingsAsync(DirectorySnapshotResult snapshot, CancellationToken cancellationToken)
    {
        foreach (var transition in snapshot.PendingChannelTransitions)
        {
            var binding = await _directoryStore.GetActiveChannelBindingAsync(
                _nodeId, transition.Slot, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _transitionSlots.Remove(transition.Slot);
                if (binding is null)
                {
                    _stableBindings.Remove(transition.Slot);
                }
                else
                {
                    _stableBindings[transition.Slot] = binding;
                }
            }
        }
    }

    private void ResolveChannel(ReceivedMessage message, out ChannelBindingRecord? binding, out byte[]? unknownIdentity)
    {
        binding = null;
        unknownIdentity = null;
        var slot = message switch
        {
            ChannelMessage channel => channel.ChannelIndex,
            ChannelDataMessage channel => channel.ChannelIndex,
            _ => (byte?)null,
        };
        if (slot is null)
        {
            return;
        }

        if (_stableBindings.TryGetValue(slot.Value, out var stable) && !_transitionSlots.Contains(slot.Value))
        {
            binding = stable;
            return;
        }

        unknownIdentity = _session!.SessionId.ToByteArray().Append((byte)0x54).Append(slot.Value).ToArray();
    }

    private void RequestDrain()
    {
        if (Interlocked.Exchange(ref _drainPending, 1) == 0)
        {
            _drainSignal.Release();
        }
    }

    private bool ConsumeDrainPending() => Interlocked.Exchange(ref _drainPending, 0) != 0;

    private void OnIngestFailed(object? sender, MessageIngestorErrorEventArgs error) =>
        _ingestFailure.TrySetResult(error.Exception);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class IngestPausedException(Exception? innerException) : Exception("Incoming message persistence is paused.", innerException);
}

public enum ReceiveCoordinatorState
{
    Created,
    Synchronizing,
    Online,
    NeedsAttention,
    Stopped,
}

public sealed class ReceiveDrainTimeoutException(string message, Exception innerException) : Exception(message, innerException);
