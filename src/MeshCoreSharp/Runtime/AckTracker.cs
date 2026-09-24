using System.Diagnostics;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;

namespace MeshCoreSharp.Runtime;

/// <summary>Per-connection ACK waiters. RX only performs bounded dictionary work.</summary>
internal sealed class AckTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<uint, PendingSend> _pending = [];
    private readonly Queue<PendingSend> _window = [];
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly MeshCoreClientOptions _options;

    public AckTracker(MeshCoreClientOptions options) => _options = options;

    public async Task<PendingSend> BeginAsync(CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var acquired = false;
        try
        {
            await _sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            while (true)
            {
                Task wait;
                lock (_sync)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    while (_window.TryPeek(out var first) && first.Released.Task.IsCompleted)
                        _window.Dequeue();
                    if (_window.Count < ProtocolLimits.ExpectedAckTableSize)
                    {
                        var pending = new PendingSend(this, linked, cancellationToken);
                        _window.Enqueue(pending);
                        return pending; // Owns the text-send gate until immediate response/failure.
                    }
                    wait = _window.Peek().Released.Task;
                }
                // Firmware uses a circular table. Counting only live waiters would allow seven
                // fast sends to wrap around and evict an older slow waiter. Keep the whole window.
                await wait.WaitAsync(linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (acquired) _sendGate.Release();
            linked.Dispose();
            throw new MeshCoreTransportException("Connection closed before sending text.");
        }
        catch
        {
            if (acquired) _sendGate.Release();
            linked.Dispose();
            throw;
        }
    }

    public void Handle(MessageAcknowledgement ack)
    {
        lock (_sync)
            if (_pending.TryGetValue(ack.Ack, out var pending))
                pending.Ack.TrySetResult(ack);
    }

    public void Stop() => _stop.Cancel();

    internal sealed class PendingSend : IDisposable
    {
        private readonly AckTracker _owner;
        private readonly CancellationTokenSource _linked;
        private readonly CancellationToken _callerToken;
        private int _sendGateReleased;
        private bool _disposed;
        private uint? _key;
        private long _boundAt;
        private TimeSpan _timeout;

        internal PendingSend(AckTracker owner, CancellationTokenSource linked, CancellationToken callerToken)
        {
            _owner = owner;
            _linked = linked;
            Token = linked.Token;
            _callerToken = callerToken;
        }

        internal TaskCompletionSource<MessageAcknowledgement> Ack { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; }

        public void Bind(MessageSentInfo info)
        {
            lock (_owner._sync)
            {
                if (_disposed) throw new OperationCanceledException(Token);
                Token.ThrowIfCancellationRequested();
                if (info.ExpectedAck == 0) return; // Firmware did not register an ACK.
                if (!_owner._pending.TryAdd(info.ExpectedAck, this))
                {
                    var error = new MeshCoreProtocolException("Duplicate expected ACK for concurrent sends; delivery is ambiguous.");
                    _owner._pending[info.ExpectedAck].Ack.TrySetException(error);
                    throw error;
                }
                _key = info.ExpectedAck;
                _boundAt = Stopwatch.GetTimestamp();
                _timeout = TimeSpan.FromMilliseconds(Math.Clamp(
                    info.SuggestedTimeoutMilliseconds + _owner._options.AckTimeoutMargin.TotalMilliseconds,
                    _owner._options.MinimumAckTimeout.TotalMilliseconds,
                    _owner._options.MaximumAckTimeout.TotalMilliseconds));
            }
        }

        public void ReleaseSendGate()
        {
            if (Interlocked.Exchange(ref _sendGateReleased, 1) == 0) _owner._sendGate.Release();
        }

        public async Task<MessageDeliveryResult> WaitForDeliveryAsync()
        {
            try
            {
                Token.ThrowIfCancellationRequested();
                if (_key is null) return new(MessageDeliveryStatus.NotExpected, null);
                var remaining = _timeout - Stopwatch.GetElapsedTime(_boundAt);
                var ack = await Ack.Task.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, Token)
                    .ConfigureAwait(false);
                return new(MessageDeliveryStatus.Confirmed, ack);
            }
            catch (TimeoutException) { return new(MessageDeliveryStatus.TimedOut, null); }
            catch (OperationCanceledException) when (!_callerToken.IsCancellationRequested)
            {
                throw new MeshCoreTransportException("Connection closed while waiting for text delivery.");
            }
            finally { Dispose(); }
        }

        public void Dispose()
        {
            lock (_owner._sync)
            {
                if (_disposed) return;
                _disposed = true;
                if (_key is { } key) _owner._pending.Remove(key);
                Released.TrySetResult();
            }
            ReleaseSendGate();
            _linked.Dispose();
        }
    }
}
