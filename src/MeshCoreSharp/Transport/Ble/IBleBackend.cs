namespace MeshCoreSharp.Transport.Ble;

internal interface IBleBackend
{
    // A bounded scan, not a connection. Callers explicitly choose a returned endpoint.
    Task<IReadOnlyList<BleDeviceInfo>> DiscoverAsync(TimeSpan duration, CancellationToken cancellationToken);

    // A successful result transfers ownership to the caller, including after caller cancellation.
    // The backend releases partially constructed resources if opening fails.
    Task<IBleGattSession> OpenAsync(string deviceId, CancellationToken cancellationToken);
}

internal interface IBleGattConnection : IAsyncDisposable
{
    // Immutable discovery results. Native backend verifies the profile and reports typed failures.
    BleGattCapabilities Capabilities { get; }
    ushort MaxPduSize { get; }
    // Physical link status only; this is not Companion readiness or transport IsConnected.
    bool IsLinkConnected { get; }
    event Action<Exception?>? Disconnected;
    event Action<ushort>? MaxPduSizeChanged;
}

internal interface IBleGattNotifications : IBleGattConnection
{

    // Notifications may arrive while enabling CCCD. The receiver must subscribe first.
    // Buffer lifetime ends when the callback returns; consumers copy before enqueueing.
    event Action<ReadOnlyMemory<byte>>? NotificationReceived;

    Task EnableNotificationsAsync(CancellationToken cancellationToken);
    Task DisableNotificationsAsync(CancellationToken cancellationToken);
}

internal interface IBleGattSession : IBleGattNotifications
{
    // One complete logical frame, WriteWithResponse. GATT success is not a Companion/mesh ACK.
    Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);

    // Dispose releases this session's native resources. Future backend must coordinate active I/O;
    // cancellation of a caller's wait alone does not imply native I/O completed.
}

internal interface IBleGattValueReader
{
    // Uncached read of the complete current TX value, including ATT Read Blob when required.
    Task<ReadOnlyMemory<byte>> ReadNotificationValueAsync(CancellationToken cancellationToken);
}

internal sealed record BleGattCapabilities(
    bool HasCompanionService,
    bool HasAppToCompanionCharacteristic,
    bool HasCompanionToAppCharacteristic,
    bool CanWriteWithResponse,
    bool CanNotify);
