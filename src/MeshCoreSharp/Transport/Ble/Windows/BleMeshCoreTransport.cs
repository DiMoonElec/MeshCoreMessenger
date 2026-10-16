namespace MeshCoreSharp.Transport.Ble;

/// <summary>Windows native BLE transport for one explicitly selected Companion endpoint.</summary>
/// <remarks>Exchanges logical frames. A successful write is not a Companion response or a mesh ACK.
/// Explicit Connect after disconnect/failure creates a new generation. Reconnect policy belongs to the application.</remarks>
public sealed class BleMeshCoreTransport : IMeshCoreTransport
{
    private readonly BleFrameTransport _transport;

    public BleMeshCoreTransport(BleMeshCoreTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var dispatcher = options.DeviceAccessDispatcher ?? throw new ArgumentException(
            "Windows BLE requires the host UI dispatcher for device access/consent.", nameof(options));
        _transport = new(options, new Windows.WindowsBleGattOpenPlatform(dispatcher), TimeProvider.System);
    }

    public bool IsConnected => _transport.IsConnected;
    /// <summary>Actual negotiated ATT MTU of the Ready generation, or null when disconnected.</summary>
    public ushort? MaxPduSize => _transport.MaxPduSize;
    /// <summary>Physical cleanup of all generations created so far, including late native I/O.
    /// Disconnect/Dispose limit their caller wait to ShutdownTimeout; inspect this task for late cleanup errors.</summary>
    public Task CleanupCompletion => _transport.CleanupCompletion;
    public Task ConnectAsync(CancellationToken cancellationToken = default) => _transport.ConnectAsync(cancellationToken);
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => _transport.DisconnectAsync(cancellationToken);
    public ValueTask SendAsync(ReadOnlyMemory<byte> companionFrame, CancellationToken cancellationToken = default) =>
        _transport.SendAsync(companionFrame, cancellationToken);
    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _transport.ReceiveAsync(cancellationToken);
    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}
