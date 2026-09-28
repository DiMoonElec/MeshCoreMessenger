using MeshCoreMessenger.Core.Persistence;

namespace MeshCoreMessenger.Core.Application;

public interface IDurableSessionCompletion
{
    bool IsPaused { get; }
    int PendingCount { get; }
    Task EndAsync(
        Guid sessionId,
        DateTimeOffset endedUtc,
        string reason,
        CancellationToken cancellationToken = default);
    Task FlushAsync(CancellationToken cancellationToken = default);
    Task RetryAsync(CancellationToken cancellationToken = default);
}

/// <summary>Keeps failed session-end writes retryable for the lifetime of the application.</summary>
public sealed class SessionCompletionTracker(ISessionStore sessions) : IDurableSessionCompletion
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, PendingSessionEnd> _pending = [];
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

    public async Task EndAsync(
        Guid sessionId,
        DateTimeOffset endedUtc,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_pending)
        {
            _pending.TryAdd(sessionId, new PendingSessionEnd(sessionId, endedUtc, reason.Trim()));
        }

        await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _failure) is { } failure)
        {
            throw new SessionCompletionPersistenceException(failure);
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
                throw new SessionCompletionPersistenceException(currentFailure);
            }

            while (TryGetPending(out var pending))
            {
                try
                {
                    await sessions.EndAsync(
                        pending.SessionId,
                        pending.EndedUtc,
                        pending.Reason,
                        cancellationToken).ConfigureAwait(false);
                    lock (_pending)
                    {
                        _pending.Remove(pending.SessionId);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Interlocked.CompareExchange(ref _failure, exception, null);
                    throw new SessionCompletionPersistenceException(exception);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryGetPending(out PendingSessionEnd pending)
    {
        lock (_pending)
        {
            if (_pending.Count == 0)
            {
                pending = default!;
                return false;
            }

            pending = _pending.Values.First();
            return true;
        }
    }

    private sealed record PendingSessionEnd(Guid SessionId, DateTimeOffset EndedUtc, string Reason);
}

public sealed class SessionCompletionPersistenceException(Exception innerException)
    : Exception("A connection session end could not be saved.", innerException);
