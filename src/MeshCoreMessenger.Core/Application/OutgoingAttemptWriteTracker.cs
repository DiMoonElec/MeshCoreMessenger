using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;

namespace MeshCoreMessenger.Core.Application;

public interface IDurableOutgoingWrites
{
    bool IsPaused { get; }
    int PendingCount { get; }
    event EventHandler? Paused;
    void ReportFailure(Exception exception);
    Task<bool> SaveAsync(OutgoingAttemptTransition transition, CancellationToken cancellationToken = default);
    Task FlushAsync(CancellationToken cancellationToken = default);
    Task RetryAsync(CancellationToken cancellationToken = default);
}

/// <summary>Retains failed status writes in order. Retry writes SQLite only; it never replays a command.</summary>
public sealed class OutgoingAttemptWriteTracker(IOutgoingMessageStore store) : IDurableOutgoingWrites
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<PendingWrite> _pending = new();
    private Exception? _failure;
    public bool IsPaused => Volatile.Read(ref _failure) is not null;
    public int PendingCount { get { lock (_pending) return _pending.Count; } }
    public event EventHandler? Paused;

    public async Task<bool> SaveAsync(OutgoingAttemptTransition transition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transition);
        cancellationToken.ThrowIfCancellationRequested();
        var write = new PendingWrite(transition with { ExpectedAck = transition.ExpectedAck?.ToArray() });
        lock (_pending) _pending.Enqueue(write);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        return write.Applied;
    }

    public void ReportFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (Interlocked.CompareExchange(ref _failure, exception, null) is not null) return;
        foreach (EventHandler handler in Paused?.GetInvocationList() ?? [])
        {
            try { handler(this, EventArgs.Empty); }
            catch { /* State observers cannot remove an uncommitted status. */ }
        }
    }

    public Task RetryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _failure, null);
        return Task.CompletedTask;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_failure is { } failure) throw new OutgoingPersistenceException(failure);
            while (TryPeek(out var write))
            {
                try
                {
                    // Once a write is accepted here, cancellation must not turn a commit into an apparent failure.
                    write.Applied = await store.TransitionAsync(write.Transition, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    ReportFailure(exception);
                    throw new OutgoingPersistenceException(exception);
                }
                lock (_pending) _pending.Dequeue();
            }
        }
        finally { _gate.Release(); }
    }

    private bool TryPeek(out PendingWrite write)
    {
        lock (_pending) return _pending.TryPeek(out write!);
    }
    private sealed class PendingWrite(OutgoingAttemptTransition transition)
    {
        public OutgoingAttemptTransition Transition { get; } = transition;
        public bool Applied { get; set; }
    }
}

public sealed class OutgoingPersistenceException(Exception innerException)
    : Exception("Outgoing status was not committed. New commands are disabled until local persistence recovers.", innerException);
