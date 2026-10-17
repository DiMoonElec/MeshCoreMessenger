using System.Buffers.Binary;
using System.Net.Sockets;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Ble;

namespace MeshCoreMessenger.Core.Tests;

// Test-only GATT facade over the existing loopback Companion emulator. Stream wrappers
// exist only behind this facade; the real BLE transport sees individual logical values.
internal sealed class BleCompanionEmulator : IBleGattOpenPlatform, IBleGattSession
{
    private readonly TcpClient _socket = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writes = new(1);
    private Task _reader = Task.CompletedTask;
    private bool _enabled;
    internal FakeCompanionServer Server { get; } = new();
    internal byte IdentitySuffix { get; set; } = 31;
    internal CommandType? DropOnCommand { get; set; }
    internal bool HoldOpen { get; set; }
    internal TaskCompletionSource Opening { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal List<byte[]> Commands { get; } = [];
    internal bool Disposed { get; private set; }
    internal BleFrameTransport Transport { get; }
    internal BleCompanionEmulator(int capacity = 256)
    {
        Server.Start();
        Transport = new(new() { DeviceId = "same-endpoint", ReceiveQueueCapacity = capacity }, this, TimeProvider.System);
    }
    public BleGattCapabilities Capabilities { get; } = new(true, true, true, true, true);
    public ushort MaxPduSize { get; private set; } = 176;
    public bool IsLinkConnected { get; private set; } = true;
    public event Action<ReadOnlyMemory<byte>>? NotificationReceived;
    public event Action<Exception?>? Disconnected;
    public event Action<ushort>? MaxPduSizeChanged;
    internal void ChangeMtu(ushort mtu) { MaxPduSize = mtu; MaxPduSizeChanged?.Invoke(mtu); }
    public async Task<IBleGattConnection?> OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        Opening.TrySetResult();
        try
        {
            if (HoldOpen) await Task.Delay(Timeout.Infinite, cancellationToken);
            await _socket.ConnectAsync("127.0.0.1", Server.Port, cancellationToken);
            return this;
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }
    public Task EnableNotificationsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _enabled = true;
        _reader = ReadAsync();
        return Task.CompletedTask;
    }
    public Task DisableNotificationsAsync(CancellationToken cancellationToken)
    {
        _enabled = false;
        return Task.CompletedTask;
    }
    public async Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        Commands.Add(frame.ToArray());
        if ((CommandType)frame.Span[0] == DropOnCommand)
        {
            Drop();
            throw new IOException("Emulated BLE link lost during write.");
        }
        await _writes.WaitAsync(cancellationToken);
        try
        {
            var wire = new byte[3 + frame.Length];
            wire[0] = 0x3C;
            BinaryPrimitives.WriteUInt16LittleEndian(wire.AsSpan(1), checked((ushort)frame.Length));
            frame.CopyTo(wire.AsMemory(3));
            await _socket.GetStream().WriteAsync(wire, cancellationToken);
        }
        finally { _writes.Release(); }
    }
    private async Task ReadAsync()
    {
        try
        {
            var stream = _socket.GetStream();
            while (!_stop.IsCancellationRequested)
            {
                var header = new byte[3];
                await stream.ReadExactlyAsync(header, _stop.Token);
                if (header[0] != 0x3E) throw new InvalidDataException("Invalid emulator framing.");
                var value = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))];
                await stream.ReadExactlyAsync(value, _stop.Token);
                // Same first 31 bytes deliberately exercise full-key identity, not prefix matching.
                if (value[0] == (byte)PacketType.SelfInfo) value[35] = IdentitySuffix;
                Emit(value);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!_stop.IsCancellationRequested) Drop();
        }
    }
    internal void Emit(byte[] value) { if (_enabled) NotificationReceived?.Invoke(value); }
    internal void Drop()
    {
        IsLinkConnected = false;
        Disconnected?.Invoke(new IOException("Emulated BLE disconnect."));
    }
    public async ValueTask DisposeAsync()
    {
        if (Disposed) return;
        Disposed = true;
        _enabled = false;
        IsLinkConnected = false;
        _stop.Cancel();
        _socket.Dispose();
        await _reader;
        await Server.DisposeAsync();
        _stop.Dispose();
        _writes.Dispose();
    }
}
