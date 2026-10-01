using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;

namespace MeshCoreMessenger.Core.Application;

public interface IDraftBuffer
{
    Task<string> LoadTextAsync(
        DraftTarget target,
        CancellationToken cancellationToken = default);

    void Update(DraftTarget target, string text, long revision);

    Task FlushAsync(
        DraftTarget target,
        CancellationToken cancellationToken = default);
}

public interface IDurableDraftWrites
{
    bool IsPaused { get; }
    Task FlushAsync(CancellationToken cancellationToken = default);
    Task RetryAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Keeps the newest accepted editor revision in memory until the serialized SQLite writer commits it.
/// Failed writes remain dirty and can be retried without restarting already quiesced application work.
/// </summary>
public sealed class DraftWriteTracker(
    IDraftStore store,
    TimeProvider timeProvider) : IDraftBuffer, IDurableDraftWrites
{
    private readonly object _gate = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private int _paused;

    public bool IsPaused => Volatile.Read(ref _paused) != 0;

    public async Task<string> LoadTextAsync(
        DraftTarget target,
        CancellationToken cancellationToken = default)
    {
        var copy = Copy(target);
        var key = Key(copy);
        lock (_gate)
        {
            if (_states.TryGetValue(key, out var existing))
            {
                return existing.Text;
            }
        }

        var stored = await store.GetAsync(copy, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_states.TryGetValue(key, out var acceptedWhileReading))
            {
                return acceptedWhileReading.Text;
            }

            _states[key] = new State(
                copy with { ConversationId = stored?.ConversationId ?? copy.ConversationId },
                stored?.Text ?? string.Empty,
                Revision: 0,
                PersistedRevision: 0);
            return stored?.Text ?? string.Empty;
        }
    }

    public void Update(DraftTarget target, string text, long revision)
    {
        var copy = Copy(target);
        ArgumentNullException.ThrowIfNull(text);
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        var key = Key(copy);
        lock (_gate)
        {
            if (_states.TryGetValue(key, out var existing) && revision <= existing.Revision)
            {
                return;
            }

            _states[key] = new State(
                copy with { ConversationId = existing?.Target.ConversationId ?? copy.ConversationId },
                text,
                revision,
                existing?.PersistedRevision ?? 0);
        }
    }

    public Task FlushAsync(
        DraftTarget target,
        CancellationToken cancellationToken = default) =>
        FlushKeysAsync([Key(Copy(target))], cancellationToken);

    Task IDurableDraftWrites.FlushAsync(CancellationToken cancellationToken) =>
        FlushAllAsync(cancellationToken);

    public async Task RetryAsync(CancellationToken cancellationToken = default)
    {
        await FlushAllAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushAllAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            string[] dirty;
            lock (_gate)
            {
                dirty = _states
                    .Where(pair => pair.Value.Revision > pair.Value.PersistedRevision)
                    .Select(pair => pair.Key)
                    .ToArray();
            }

            if (dirty.Length == 0)
            {
                Interlocked.Exchange(ref _paused, 0);
                return;
            }

            await FlushKeysAsync(dirty, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FlushKeysAsync(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var key in keys)
            {
                while (true)
                {
                    State snapshot;
                    lock (_gate)
                    {
                        if (!_states.TryGetValue(key, out snapshot!) ||
                            snapshot.Revision <= snapshot.PersistedRevision)
                        {
                            break;
                        }
                    }

                    DraftRecord? saved;
                    try
                    {
                        saved = await store.SaveAsync(
                            snapshot.Target,
                            snapshot.Text,
                            timeProvider.GetUtcNow(),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        Interlocked.Exchange(ref _paused, 1);
                        throw;
                    }

                    lock (_gate)
                    {
                        if (_states.TryGetValue(key, out var current))
                        {
                            _states[key] = current with
                            {
                                Target = current.Target with
                                {
                                    ConversationId = saved?.ConversationId ?? current.Target.ConversationId,
                                },
                                PersistedRevision = Math.Max(
                                    current.PersistedRevision,
                                    snapshot.Revision),
                            };
                        }
                    }
                }
            }

            lock (_gate)
            {
                if (_states.Values.All(state => state.Revision <= state.PersistedRevision))
                {
                    Interlocked.Exchange(ref _paused, 0);
                }
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private static DraftTarget Copy(DraftTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Identity is null)
        {
            throw new ArgumentException("Draft identity is required.", nameof(target));
        }

        return target with { Identity = target.Identity.ToArray() };
    }

    private static string Key(DraftTarget target) =>
        $"{target.NodeId:D}:{(int)target.Kind}:{Convert.ToHexString(target.Identity)}";

    private sealed record State(
        DraftTarget Target,
        string Text,
        long Revision,
        long PersistedRevision);
}
