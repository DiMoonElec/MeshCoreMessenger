using System.Threading.Channels;
using MeshCoreSharp.Transport.Ble;

// Completion is deliberately controlled independently of cancellation: Windows may finish I/O late.
internal sealed class ControlledBleOperation<T>(CancellationToken cancellationToken)
{
    private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationToken CancellationToken { get; } = cancellationToken;
    internal Task<T> Task => _completion.Task;
    internal void Complete(T result) => _completion.SetResult(result);
    internal void Fail(Exception error) => _completion.SetException(error);
    internal void Cancel() => _completion.SetCanceled(CancellationToken);
}

internal sealed class FakeBleBackend : IBleBackend
{
    internal sealed record Discovery(TimeSpan Duration, ControlledBleOperation<IReadOnlyList<BleDeviceInfo>> Operation);
    internal sealed record Open(string DeviceId, ControlledBleOperation<IBleGattSession> Operation);
    internal Channel<Discovery> Discoveries { get; } = Channel.CreateUnbounded<Discovery>();
    internal Channel<Open> Opens { get; } = Channel.CreateUnbounded<Open>();

    public Task<IReadOnlyList<BleDeviceInfo>> DiscoverAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var operation = new ControlledBleOperation<IReadOnlyList<BleDeviceInfo>>(cancellationToken);
        Discoveries.Writer.TryWrite(new(duration, operation));
        return operation.Task;
    }

    public Task<IBleGattSession> OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        var operation = new ControlledBleOperation<IBleGattSession>(cancellationToken);
        Opens.Writer.TryWrite(new(deviceId, operation));
        return operation.Task;
    }
}

internal sealed class FakeBleGattSession : IBleGattSession
{
    internal sealed record Write(ReadOnlyMemory<byte> Frame, ControlledBleOperation<bool> Operation);
    public BleGattCapabilities Capabilities { get; init; } = new(true, true, true, true, true);
    public ushort MaxPduSize { get; private set; } = 247;
    public event Action<ReadOnlyMemory<byte>>? NotificationReceived;
    public event Action<Exception?>? Disconnected;
    public event Action<ushort>? MaxPduSizeChanged;

    internal Channel<ControlledBleOperation<bool>> Subscriptions { get; } = Channel.CreateUnbounded<ControlledBleOperation<bool>>();
    internal Channel<ControlledBleOperation<bool>> Unsubscriptions { get; } = Channel.CreateUnbounded<ControlledBleOperation<bool>>();
    internal Channel<Write> Writes { get; } = Channel.CreateUnbounded<Write>();
    internal ControlledBleOperation<bool>? Disposal { get; init; }
    internal int DisposeCount;

    public Task EnableNotificationsAsync(CancellationToken cancellationToken) => Queue(Subscriptions, cancellationToken);
    public Task DisableNotificationsAsync(CancellationToken cancellationToken) => Queue(Unsubscriptions, cancellationToken);

    private static Task Queue(Channel<ControlledBleOperation<bool>> queue, CancellationToken cancellationToken)
    {
        var operation = new ControlledBleOperation<bool>(cancellationToken);
        queue.Writer.TryWrite(operation);
        return operation.Task;
    }

    public Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        var operation = new ControlledBleOperation<bool>(cancellationToken);
        Writes.Writer.TryWrite(new(frame.ToArray(), operation));
        return operation.Task;
    }

    internal void EmitNotification(ReadOnlyMemory<byte> frame) => NotificationReceived?.Invoke(frame);
    internal void EmitDisconnect(Exception? error = null) => Disconnected?.Invoke(error);
    internal void EmitMaxPduSize(ushort size)
    {
        MaxPduSize = size;
        MaxPduSizeChanged?.Invoke(size);
    }

    // Retained callbacks simulate events already dispatched before handlers were removed.
    internal (Action<ReadOnlyMemory<byte>>? Notification, Action<Exception?>? Disconnect, Action<ushort>? Mtu)
        CaptureCallbacks() => (NotificationReceived, Disconnected, MaxPduSizeChanged);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref DisposeCount);
        NotificationReceived = null;
        Disconnected = null;
        MaxPduSizeChanged = null;
        return Disposal is null ? ValueTask.CompletedTask : new(Disposal.Task);
    }
}
