using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;

namespace MeshCoreMessenger.Core.Application;

public interface IDurableReadStateWrites
{
    bool IsPaused { get; }
    int PendingCount { get; }
    Task<ConversationReadState> AdvanceAsync(
        HistoryMessagePosition through,
        CancellationToken cancellationToken = default);
    Task FlushAsync(CancellationToken cancellationToken = default);
    Task RetryAsync(CancellationToken cancellationToken = default);
}

/// <summary>Keeps accepted read-watermark writes serialized and retryable.</summary>
public sealed class ConversationReadStateTracker(IConversationReadStateStore store)
    : IDurableReadStateWrites
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(Guid NodeId, Guid ConversationId), HistoryMessagePosition> _pending = [];
    private readonly Dictionary<(Guid NodeId, Guid ConversationId), ConversationReadState> _latest = [];
    private Exception? _failure;

    public bool IsPaused => Volatile.Read(ref _failure) is not null;

    public int PendingCount
    {
        get
        {
            lock (_pending)
            {
                return _pending.Count;
            }
        }
    }

    public async Task<ConversationReadState> AdvanceAsync(
        HistoryMessagePosition through,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(through);
        var key = (through.NodeId, through.ConversationId);
        lock (_pending)
        {
            if (!_pending.TryGetValue(key, out var current) ||
                through.LocalSequence > current.LocalSequence)
            {
                _pending[key] = through;
            }
        }

        await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
        lock (_pending)
        {
            return _latest.TryGetValue(key, out var state)
                ? state
                : throw new InvalidOperationException("The accepted read-state write produced no result.");
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _failure) is { } failure)
        {
            throw new ReadStatePersistenceException(failure);
        }

        return FlushCoreAsync(cancellationToken);
    }

    public Task RetryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _failure, null);
        return Task.CompletedTask;
    }

    private async Task FlushCoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_failure is { } currentFailure)
            {
                throw new ReadStatePersistenceException(currentFailure);
            }

            while (TryGetPending(out var pending))
            {
                ConversationReadState state;
                try
                {
                    state = await store.AdvanceAsync(pending, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Interlocked.CompareExchange(ref _failure, exception, null);
                    throw new ReadStatePersistenceException(exception);
                }

                var key = (pending.NodeId, pending.ConversationId);
                lock (_pending)
                {
                    _latest[key] = state;
                    if (_pending.TryGetValue(key, out var current) &&
                        current.LocalSequence <= pending.LocalSequence)
                    {
                        _pending.Remove(key);
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryGetPending(out HistoryMessagePosition position)
    {
        lock (_pending)
        {
            if (_pending.Count == 0)
            {
                position = default!;
                return false;
            }

            position = _pending.Values.First();
            return true;
        }
    }
}

public sealed class ReadStatePersistenceException(Exception innerException)
    : Exception("A conversation read position could not be saved.", innerException);
