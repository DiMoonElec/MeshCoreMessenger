using MeshCoreSharp.Exceptions;

namespace MeshCoreSharp.Transport.Ble;

// Common logical-frame implementation, testable without Windows or a radio.
// A fresh instance owns one attempt. Reconnect/lifecycle consolidation is BLE7.
internal sealed class BleFrameTransport(BleMeshCoreTransportOptions options, IBleGattOpenPlatform platform,
    TimeProvider time) : IMeshCoreTransport
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private BleReceiveSession? _receiver;
    private Task? _connect;
    private bool _closed;
    internal Task CleanupCompletion { get; private set; } = Task.CompletedTask;
    public bool IsConnected { get { lock (_sync) return !_closed && _receiver?.IsConnected == true; } }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_closed || _connect is not null)
                throw new InvalidOperationException("Use a new BLE transport for each connection attempt.");
            return _connect = OpenAsync(cancellationToken);
        }
    }

    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        var opener = new BleGattSessionOpener(platform, time);
        using var deadline = new CancellationTokenSource(options.OpenTimeout, time);
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, _lifetime.Token);
        BleGattOpenResult? prepared = null;
        try
        {
            prepared = await opener.OpenAsync(options, opening.Token).ConfigureAwait(false);
            var receiver = new BleReceiveSession(prepared, options, time);
            lock (_sync) _receiver = receiver;
            opening.Token.ThrowIfCancellationRequested();
            await receiver.StartAsync(opening.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var failure = error is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? new OperationCanceledException(cancellationToken)
                : error is OperationCanceledException && deadline.IsCancellationRequested
                    ? new MeshCoreBleTransportException(MeshCoreBleFailureReason.DeviceUnreachable,
                        "BLE opening and notification subscription exceeded the open deadline.", error)
                    : error;
            lock (_sync)
            {
                _closed = true;
                _receiver?.Stop(failure);
                CleanupCompletion = _receiver?.CleanupCompletion
                    ?? prepared?.Attempt.CloseAsync(failure) ?? opener.CleanupCompletion;
                BleReceiveSession.Observe(CleanupCompletion);
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> companionFrame, CancellationToken cancellationToken = default)
    {
        lock (_sync)
            return (_receiver ?? throw new MeshCoreTransportException("BLE is not connected."))
                .SendAsync(companionFrame, cancellationToken);
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
            return (_receiver ?? throw new MeshCoreTransportException("BLE is not connected."))
                .ReceiveAsync(cancellationToken);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _closed = true;
            _receiver?.Stop();
            CleanupCompletion = _receiver?.CleanupCompletion ?? CleanupCompletion;
        }
        _lifetime.Cancel();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());
}
