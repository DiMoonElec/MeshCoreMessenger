namespace MeshCoreSharp.Transport.Ble;

internal enum BleConnectionState { Closed, Opening, Ready, Closing, Faulted }

/// <summary>
/// Ownership ledger for one connection generation, not a transport or reconnect loop.
/// Only the future transport may mark it Ready after GATT validation and CCCD success.
/// Late open results are disposed here; late callbacks must be gated on this attempt's state.
/// </summary>
internal sealed class BleConnectionAttempt : IAsyncDisposable
{
    private static long _nextGeneration;
    private readonly object _sync = new();
    private IBleGattConnection? _session;
    private bool _started;
    private Task? _cleanup;
    private BleConnectionState _state;
    private Exception? _error;

    internal BleConnectionAttempt(BleMeshCoreTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Generation = Interlocked.Increment(ref _nextGeneration);
    }

    internal long Generation { get; }
    internal BleConnectionState State { get { lock (_sync) return _state; } }
    internal Exception? Error { get { lock (_sync) return _error; } }

    internal void BeginOpening()
    {
        lock (_sync)
        {
            if (_started || _cleanup is not null)
                throw new InvalidOperationException("Create a new attempt for each connection generation.");
            _started = true;
            _state = BleConnectionState.Opening;
        }
    }

    // Takes ownership even on rejection, so cancelled/late results cannot leak.
    internal async ValueTask<bool> AdoptSessionAsync(IBleGattConnection session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_sync)
        {
            if (_state == BleConnectionState.Opening && _session is null)
            {
                _session = session;
                return true;
            }
            if (ReferenceEquals(_session, session))
                throw new InvalidOperationException("The session is already owned by this attempt.");
        }
        await session.DisposeAsync().ConfigureAwait(false);
        return false;
    }

    internal bool TryMarkReadyAfterSubscription()
    {
        lock (_sync)
        {
            if (_state != BleConnectionState.Opening || _session is null)
                return false;
            _state = BleConnectionState.Ready;
            return true;
        }
    }

    internal Task CloseAsync(Exception? error = null)
    {
        TaskCompletionSource completion;
        IBleGattConnection? session;
        lock (_sync)
        {
            if (_cleanup is not null)
                return _cleanup;
            _state = BleConnectionState.Closing;
            _error = error;
            session = _session;
            _session = null;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _cleanup = completion.Task;
        }
        // No native disposal or synchronous continuation runs under the state lock.
        _ = CompleteCleanupAsync(session, completion);
        return completion.Task;
    }

    private async Task CompleteCleanupAsync(IBleGattConnection? session, TaskCompletionSource completion)
    {
        try
        {
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
            lock (_sync)
                _state = _error is null ? BleConnectionState.Closed : BleConnectionState.Faulted;
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            lock (_sync)
            {
                _error ??= error;
                _state = BleConnectionState.Faulted;
            }
            completion.TrySetException(error);
        }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}
