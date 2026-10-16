using System.Buffers.Binary;
using MeshCoreSharp;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Packets;
using MeshCoreSharp.Transport.Ble;

internal static class BleSendTests
{
    private static BleMeshCoreTransportOptions Options() => BleReceiveTests.Options();
    private static void Check(bool value) => BleReceiveTests.Check(value);
    private static Task<T> Throws<T>(Func<Task> action) where T : Exception => BleReceiveTests.Throws<T>(action);
    private static Task<(BleReceiveSession Receiver, FakeBleGattSession Native)> Ready() => BleReceiveTests.Ready();
    private static Task<(BleReceiveSession Receiver, FakeBleGattSession Native)> Prepare() => BleReceiveTests.Prepare();
    internal static (string Name, Func<Task> Run)[] Cases =>
    [
        ("BLE TX requires Ready and validates whole frame sizes before writing", Validation),
        ("BLE TX sends exact logical bytes once at the firmware size limit", ExactBytes),
        ("BLE TX serializes writes and queued cancellation leaves the link healthy", Serialized),
        ("BLE TX cancellation after start faults without retry or late revival", CancelAfterStart),
        ("BLE TX write deadline faults while observing late native failure", WriteDeadline),
        ("BLE TX GATT failure preserves native diagnostics and faults RX", GattFailure),
        ("BLE TX disconnect during write reports unknown outcome", DisconnectWrite),
        ("BLE transport shares one open deadline through CCCD", OpenDeadline),
        ("BLE MeshCoreClient accepts responses and ACK before GATT write completes", ClientExchange),
    ];

    private static async Task Validation()
    {
        foreach (var timeout in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(-1), TimeSpan.MaxValue })
            await Throws<ArgumentOutOfRangeException>(() =>
            {
                (Options() with { WriteTimeout = timeout }).Validate();
                return Task.CompletedTask;
            });
        var (opening, pendingNative) = await Prepare();
        await using var pendingOwner = opening;
        await Throws<MeshCoreTransportException>(() => opening.SendAsync(new byte[] { 1 }, default).AsTask());
        Check(!pendingNative.Writes.Reader.TryRead(out _));
        var (ready, native) = await Ready();
        await using var owner = ready;
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[Options().MaximumFrameSize + 1] })
            await Throws<ArgumentOutOfRangeException>(() => ready.SendAsync(bytes, default).AsTask());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var error = await Throws<OperationCanceledException>(() => ready.SendAsync(new byte[] { 1 }, cancelled.Token).AsTask());
        Check(error.CancellationToken == cancelled.Token && ready.IsConnected && !native.Writes.Reader.TryRead(out _));
    }

    private static async Task ExactBytes()
    {
        var (receiver, native) = await Ready();
        await using var owner = receiver;
        foreach (var bytes in new byte[][] { [0x3C], Enumerable.Range(0, ProtocolLimits.CurrentFirmwareMaxFrameSize).Select(i => (byte)i).ToArray() })
        {
            var send = receiver.SendAsync(bytes, default).AsTask();
            var write = await native.Writes.Reader.ReadAsync();
            Check(write.Frame.Span.SequenceEqual(bytes) && !send.IsCompleted);
            native.EmitNotification(new byte[] { 0x80 });
            await using var reader = receiver.ReceiveAsync().GetAsyncEnumerator();
            Check(await reader.MoveNextAsync() && reader.Current.Span[0] == 0x80 && !send.IsCompleted);
            write.Operation.Complete(true);
            await send;
            Check(!native.Writes.Reader.TryRead(out _));
        }
    }

    private static async Task Serialized()
    {
        var (receiver, native) = await Ready();
        await using var owner = receiver;
        var first = receiver.SendAsync(new byte[] { 1 }, default).AsTask();
        var firstWrite = await native.Writes.Reader.ReadAsync();
        using var cancel = new CancellationTokenSource();
        var skipped = receiver.SendAsync(new byte[] { 2 }, cancel.Token).AsTask();
        var next = receiver.SendAsync(new byte[] { 3 }, default).AsTask();
        Check(!native.Writes.Reader.TryRead(out _));
        cancel.Cancel();
        await Throws<OperationCanceledException>(() => skipped);
        Check(receiver.IsConnected && !native.Writes.Reader.TryRead(out _));
        firstWrite.Operation.Complete(true);
        await first;
        var secondWrite = await native.Writes.Reader.ReadAsync();
        Check(secondWrite.Frame.Span.SequenceEqual(new byte[] { 3 }));
        secondWrite.Operation.Complete(true);
        await next;
    }

    private static async Task CancelAfterStart()
    {
        var (receiver, native) = await Ready();
        await using var owner = receiver;
        using var cancel = new CancellationTokenSource();
        byte[] buffer = [0x02, 7];
        var sending = receiver.SendAsync(buffer, cancel.Token).AsTask();
        var write = await native.Writes.Reader.ReadAsync();
        var queued = receiver.SendAsync(new byte[] { 3 }, default).AsTask();
        cancel.Cancel();
        var error = await Throws<OperationCanceledException>(() => sending);
        Check(error.CancellationToken == cancel.Token && !receiver.IsConnected);
        buffer[1] = 99;
        Check(write.Frame.Span[1] == 7);
        await Throws<MeshCoreBleTransportException>(() => queued);
        await using var reader = receiver.ReceiveAsync().GetAsyncEnumerator();
        var terminal = await Throws<MeshCoreBleTransportException>(async () => await reader.MoveNextAsync());
        Check(terminal.Reason == MeshCoreBleFailureReason.WriteOutcomeUnknown);
        write.Operation.Complete(true);
        await receiver.CleanupCompletion;
        Check(!receiver.IsConnected && !native.Writes.Reader.TryRead(out _));
    }

    private static async Task WriteDeadline()
    {
        var options = Options() with { WriteTimeout = TimeSpan.FromSeconds(2) };
        var clock = new BleDiscoveryTestClock();
        var native = new FakeBleGattSession();
        var attempt = new BleConnectionAttempt(options);
        attempt.BeginOpening();
        await attempt.AdoptSessionAsync(native);
        await using var receiver = new BleReceiveSession(new(attempt, native), options, clock);
        var start = receiver.StartAsync(default);
        (await native.Subscriptions.Reader.ReadAsync()).Complete(true);
        await start;
        var send = receiver.SendAsync(new byte[] { 1 }, default).AsTask();
        var write = await native.Writes.Reader.ReadAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        var error = await Throws<MeshCoreBleTransportException>(() => send);
        Check(error.Reason == MeshCoreBleFailureReason.WriteOutcomeUnknown && !receiver.IsConnected);
        write.Operation.Fail(new IOException("Late native write fault"));
        await receiver.CleanupCompletion;
        Check(!native.Writes.Reader.TryRead(out _));
    }

    private static async Task GattFailure()
    {
        var (receiver, native) = await Ready();
        await using var owner = receiver;
        var send = receiver.SendAsync(new byte[] { 1 }, default).AsTask();
        var write = await native.Writes.Reader.ReadAsync();
        write.Operation.Fail(new MeshCoreBleTransportException(MeshCoreBleFailureReason.PairingRequired,
            "Authentication required", nativeStatus: 2, protocolError: 5));
        var error = await Throws<MeshCoreBleTransportException>(() => send);
        Check(error.Reason == MeshCoreBleFailureReason.PairingRequired && error.NativeStatus == 2 && error.ProtocolError == 5);
        Check(!receiver.IsConnected && !native.Writes.Reader.TryRead(out _));
        await using var reader = receiver.ReceiveAsync().GetAsyncEnumerator();
        Check(ReferenceEquals(error, await Throws<MeshCoreBleTransportException>(async () => await reader.MoveNextAsync())));
    }

    private static async Task DisconnectWrite()
    {
        var (receiver, native) = await Ready();
        await using var owner = receiver;
        var send = receiver.SendAsync(new byte[] { 1 }, default).AsTask();
        var write = await native.Writes.Reader.ReadAsync();
        native.EmitDisconnect();
        var error = await Throws<MeshCoreBleTransportException>(() => send);
        Check(error.Reason == MeshCoreBleFailureReason.WriteOutcomeUnknown && !receiver.IsConnected);
        write.Operation.Complete(true);
        Check(!native.Writes.Reader.TryRead(out _));
    }

    private static async Task OpenDeadline()
    {
        var clock = new BleDiscoveryTestClock();
        var platform = new FakeBleGattOpenPlatform();
        var options = Options() with { OpenTimeout = TimeSpan.FromSeconds(3) };
        await using var transport = new BleFrameTransport(options, platform, clock);
        var connect = transport.ConnectAsync();
        var open = await platform.Opens.Reader.ReadAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        var native = new FakeBleGattSession();
        open.Operation.Complete(native);
        var subscription = await native.Subscriptions.Reader.ReadAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var error = await Throws<MeshCoreBleTransportException>(() => connect);
        Check(error.Reason == MeshCoreBleFailureReason.DeviceUnreachable && !transport.IsConnected);
        subscription.Complete(true);
        await transport.CleanupCompletion;
        Check(!transport.IsConnected && native.DisposeCount == 1);
    }

    private static async Task ClientExchange()
    {
        var platform = new FakeBleGattOpenPlatform();
        await using var transport = new BleFrameTransport(Options(), platform, TimeProvider.System);
        await using var client = new MeshCoreClient(transport, new MeshCoreClientOptions
        {
            AutoReceiveMessages = false, CommandTimeout = TimeSpan.FromSeconds(3),
        });
        var connect = client.ConnectAsync();
        var opening = await platform.Opens.Reader.ReadAsync();
        var native = new FakeBleGattSession();
        opening.Operation.Complete(native);
        (await native.Subscriptions.Reader.ReadAsync()).Complete(true);
        await connect;
        var start = client.StartAsync();
        var appStart = await native.Writes.Reader.ReadAsync();
        Check(appStart.Frame.Span[0] == (byte)CommandType.AppStart);
        byte[] self = new byte[58];
        self[0] = (byte)PacketType.SelfInfo;
        native.EmitNotification(self);
        appStart.Operation.Complete(true);
        await start;

        var seenAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PacketReceived += (_, e) => { if (e.Packet is AckPacket) seenAck.TrySetResult(); };
        var sending = client.SendTextAsync(new byte[32], "BLE fake test");
        var write = await native.Writes.Reader.ReadAsync();
        Check(write.Frame.Span[0] == (byte)CommandType.SendTextMessage);
        byte[] sent = new byte[10];
        sent[0] = (byte)PacketType.MessageSent;
        sent[1] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(sent.AsSpan(2), 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(sent.AsSpan(6), 1000);
        byte[] ack = new byte[9];
        ack[0] = (byte)PacketType.Ack;
        BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(1), 0x12345678);
        native.EmitNotification(sent);
        native.EmitNotification(ack);
        await seenAck.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!sending.IsCompleted); // GATT remains pending while RX and ACK routing continue.
        write.Operation.Complete(true);
        var result = await sending;
        Check((await result.Delivery).Status == MessageDeliveryStatus.Confirmed);

        var readingTime = client.GetDeviceTimeAsync();
        var timeWrite = await native.Writes.Reader.ReadAsync();
        Check(timeWrite.Frame.Span.SequenceEqual(new byte[] { (byte)CommandType.GetDeviceTime }));
        timeWrite.Operation.Complete(true);
        // Native success alone supplies no Companion response. Interleaved unknown push is harmless.
        Check(!readingTime.IsCompleted);
        native.EmitNotification(new byte[] { 0xFE, 7 });
        native.EmitNotification(Fixtures.Number(PacketType.CurrentTime, 42));
        Check((await readingTime).ToUnixTimeSeconds() == 42);
        Check(!native.Writes.Reader.TryRead(out _));
    }
}
