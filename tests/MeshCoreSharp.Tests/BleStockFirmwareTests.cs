using System.Text;
using MeshCoreSharp;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Protocol.Packets;
using MeshCoreSharp.Protocol.Parsing;
using MeshCoreSharp.Transport.Ble;

internal static class BleStockFirmwareTests
{
    internal static (string Name, Func<Task> Run)[] Cases =>
    [
        ("BLE stock budgets separate ATT, Companion, sender and radio region overhead", Budgets),
        ("BLE stock text uses UTF-8 bytes and never silently trims", TextBytes),
        ("BLE stock MTU changes update budgets and reject queued oversize writes locally", ChangedMtu),
        ("BLE stock full V3 private text is recovered using a complete TX read", LongText),
        ("BLE stock long signed contact and channel text preserve their complete payloads", LongKinds),
        ("BLE stock exact ATT-boundary text is verified without guessing truncation", ExactBoundary),
        ("BLE stock already-complete long notification is preserved", CompleteValue),
        ("BLE stock mismatched TX read is rejected before partial delivery", MismatchedValue),
        ("BLE stock a newer notification before or during read rejects ambiguous recovery", NewerValue),
        ("BLE stock read failure and incomplete UTF-8 never produce a partial message", InvalidRead),
        ("BLE stock receive timeout retains native read ownership until its late completion", ReadTimeout),
        ("BLE stock receive cancellation preserves its token and never disposes under a read", CancelRead),
        ("BLE stock MeshCoreClient drains a recovered long incoming message completely", ClientDrain),
    ];

    private static void Check(bool value) => BleReceiveTests.Check(value);
    private static Task<T> Throws<T>(Func<Task> action) where T : Exception => BleReceiveTests.Throws<T>(action);
    private static BleMeshCoreTransportOptions Options => BleReceiveTests.Options();
    private static async Task<(BleReceiveSession Receiver, FakeBleGattSession Native)> Ready(ushort mtu = 176, TimeProvider? clock = null)
    {
        var native = new FakeBleGattSession();
        native.EmitMaxPduSize(mtu);
        var attempt = new BleConnectionAttempt(Options);
        attempt.BeginOpening();
        await attempt.AdoptSessionAsync(native);
        var receiver = new BleReceiveSession(new(attempt, native), Options, clock);
        var start = receiver.StartAsync(default);
        (await native.Subscriptions.Reader.ReadAsync()).Complete(true);
        await start;
        receiver.SetSenderName("Я🐈");
        return (receiver, native);
    }

    private static Task Budgets()
    {
        var p = BleTextBudget.Calculate(176, BleTextMessageKind.Private);
        Check(p.MaximumTextBytes == 160 && p.AttPayloadBytes == 173 && p.CompanionHeaderBytes == 13);
        var c = BleTextBudget.Calculate(176, BleTextMessageKind.Channel, "Я🐈");
        Check(c.SenderPrefixBytes == 8 && c.MaximumTextBytes == 152 && c.CompanionHeaderBytes == 7);
        Check(c.RegionOverheadBytes == 4 && c.MaximumTextBytes ==
            BleTextBudget.Calculate(176, BleTextMessageKind.Channel, "Я🐈", regionScoped: false).MaximumTextBytes);
        Check(BleTextBudget.Calculate(23, BleTextMessageKind.Private).MaximumTextBytes == 7);
        Check(BleTextBudget.Calculate(23, BleTextMessageKind.Channel, "Я🐈").MaximumTextBytes == 13);
        Check(BleTextBudget.Calculate(160, BleTextMessageKind.Private).MaximumTextBytes == 144);
        Check(BleTextBudget.Calculate(247, BleTextMessageKind.Private).MaximumTextBytes == 160);
        Check(BleTextBudget.Calculate(247, BleTextMessageKind.Channel, new string('я', 80)).MaximumTextBytes == 0);
        return Task.CompletedTask;
    }

    private static async Task TextBytes()
    {
        var (receiver, native) = await Ready(160);
        await using var owner = receiver;
        foreach (var text in new[] { new string('x', 144), new string('я', 72), string.Concat(Enumerable.Repeat("🐈", 36)) })
        {
            var frame = CompanionCommands.SendText(new byte[32], text, 1);
            var send = receiver.SendAsync(frame, default).AsTask();
            var write = await native.Writes.Reader.ReadAsync();
            Check(write.Frame.Span.SequenceEqual(frame) && write.Frame.Length == 157);
            write.Operation.Complete(true);
            await send;
        }
        foreach (var text in new[] { new string('x', 145), new string('я', 73), string.Concat(Enumerable.Repeat("🐈", 37)) })
            await Throws<ArgumentOutOfRangeException>(() => receiver.SendAsync(
                CompanionCommands.SendText(new byte[32], text, 1), default).AsTask());
        native.EmitMaxPduSize(176);
        var channel = CompanionCommands.SendChannelText(0, "Я🐈", new string('x', 152), 1);
        var sending = receiver.SendAsync(channel, default).AsTask();
        var pending = await native.Writes.Reader.ReadAsync();
        Check(pending.Frame.Length == 159);
        pending.Operation.Complete(true); await sending;
        // Raw logical callers receive the same sender-prefix limit as the high-level API.
        byte[] oversized = [.. channel, (byte)'x'];
        await Throws<ArgumentOutOfRangeException>(() => receiver.SendAsync(oversized, default).AsTask());
        Check(receiver.IsConnected && !native.Writes.Reader.TryRead(out _));
    }

    private static async Task ChangedMtu()
    {
        var (receiver, native) = await Ready();
        await using var owner = receiver;
        var frame = CompanionCommands.SendText(new byte[32], new string('x', 160), 1);
        var first = receiver.SendAsync(frame, default).AsTask();
        var write = await native.Writes.Reader.ReadAsync();
        var queued = receiver.SendAsync(frame, default).AsTask();
        native.EmitMaxPduSize(160);
        write.Operation.Complete(true); await first;
        await Throws<ArgumentOutOfRangeException>(() => queued);
        Check(receiver.IsConnected && receiver.GetTextBudget(BleTextMessageKind.Private).MaximumTextBytes == 144
            && !native.Writes.Reader.TryRead(out _));
    }

    private static byte[] LongFrame() => MessageFixtures.Text(PacketType.ContactMessageReceivedV3,
        new string('x', 156) + "🐈");

    private static async Task LongText()
    {
        var (receiver, native) = await Ready();
        await using var owner = receiver;
        var complete = LongFrame(); Check(complete.Length == 176);
        native.EmitNotification(complete.AsMemory(0, 173)); // Deliberately cuts the final emoji's UTF-8 sequence.
        await using var reader = receiver.ReceiveAsync().GetAsyncEnumerator();
        var moving = reader.MoveNextAsync().AsTask();
        var read = await native.Reads.Reader.ReadAsync();
        Check(!moving.IsCompleted);
        read.Complete(complete);
        Check(await moving && reader.Current.Span.SequenceEqual(complete));
        var decoded = (ReceivedMessagePacket)new CompanionPacketDecoder().Decode(reader.Current);
        Check(((ContactMessage)decoded.Message).Text == new string('x', 156) + "🐈");
    }

    private static async Task ExactBoundary()
    {
        var (receiver, native) = await Ready();
        await using var owner = receiver;
        var complete = MessageFixtures.Text(PacketType.ContactMessageReceived, new string('x', 160));
        Check(complete.Length == 173);
        native.EmitNotification(complete);
        await using var reader = receiver.ReceiveAsync().GetAsyncEnumerator();
        var moving = reader.MoveNextAsync().AsTask();
        (await native.Reads.Reader.ReadAsync()).Complete(complete);
        Check(await moving && reader.Current.Length == 173);
    }

    private static async Task LongKinds()
    {
        foreach (var complete in new[]
        {
            MessageFixtures.Text(PacketType.ContactMessageReceived, new string('x', 155) + "🐈", textType: 2),
            MessageFixtures.Text(PacketType.ChannelMessageReceivedV3, new string('x', 161) + "🐈"),
        })
        {
            var (receiver, native) = await Ready();
            await using var owner = receiver;
            Check(complete.Length == 176);
            native.EmitNotification(complete.AsMemory(0, 173));
            await using var reader = receiver.ReceiveAsync().GetAsyncEnumerator();
            var moving = reader.MoveNextAsync().AsTask();
            (await native.Reads.Reader.ReadAsync()).Complete(complete);
            Check(await moving && reader.Current.Span.SequenceEqual(complete));
            Check(new CompanionPacketDecoder().Decode(reader.Current) is ReceivedMessagePacket);
        }
    }

    private static async Task CompleteValue()
    {
        var (receiver, native) = await Ready();
        await using var owner = receiver;
        native.EmitNotification(LongFrame());
        await using var reader = receiver.ReceiveAsync().GetAsyncEnumerator();
        Check(await reader.MoveNextAsync() && reader.Current.Length == 176 && !native.Reads.Reader.TryRead(out _));
    }

    private static async Task MismatchedValue()
    {
        var changed = LongFrame(); changed[20] = (byte)'y';
        foreach (var value in new[] { new byte[] { 0x83 }, LongFrame()[..172], changed, LongFrame().Append((byte)0).ToArray() })
        {
            var (receiver, native) = await Ready();
            await using var owner = receiver;
            native.EmitNotification(LongFrame().AsMemory(0, 173));
            await using var reader = receiver.ReceiveAsync().GetAsyncEnumerator();
            var moving = reader.MoveNextAsync().AsTask();
            (await native.Reads.Reader.ReadAsync()).Complete(value);
            Check((await Throws<MeshCoreBleTransportException>(() => moving)).Reason == MeshCoreBleFailureReason.IncompleteNotification);
            Check(!receiver.IsConnected);
        }
    }

    private static async Task NewerValue()
    {
        foreach (var before in new[] { false, true })
        {
            var (receiver, native) = await Ready();
            await using var owner = receiver;
            native.EmitNotification(LongFrame().AsMemory(0, 173));
            if (before) native.EmitNotification(new byte[] { 0x83 });
            await using var reader = receiver.ReceiveAsync().GetAsyncEnumerator();
            var moving = reader.MoveNextAsync().AsTask();
            if (!before)
            {
                var read = await native.Reads.Reader.ReadAsync();
                native.EmitNotification(new byte[] { 0x83 });
                read.Complete(LongFrame());
            }
            Check((await Throws<MeshCoreBleTransportException>(() => moving)).Reason == MeshCoreBleFailureReason.IncompleteNotification);
            if (before) Check(!native.Reads.Reader.TryRead(out _));
        }
    }

    private static async Task InvalidRead()
    {
        var (receiver, native) = await Ready();
        await using var owner = receiver;
        native.EmitNotification(LongFrame().AsMemory(0, 173));
        await using var reader = receiver.ReceiveAsync().GetAsyncEnumerator();
        var moving = reader.MoveNextAsync().AsTask();
        (await native.Reads.Reader.ReadAsync()).Fail(new IOException("GATT read failed"));
        await Throws<MeshCoreBleTransportException>(() => moving);
        var (other, otherNative) = await Ready();
        await using var otherOwner = other;
        var invalid = MessageFixtures.Text(PacketType.ContactMessageReceivedV3, "x");
        invalid[^1] = 0xD0;
        otherNative.EmitNotification(invalid);
        await using var otherReader = other.ReceiveAsync().GetAsyncEnumerator();
        await Throws<MeshCoreBleTransportException>(async () => await otherReader.MoveNextAsync());
        // The common parser keeps its previous policy: strict UTF-8 protection is BLE-only.
        Check(((ContactMessage)((ReceivedMessagePacket)new CompanionPacketDecoder().Decode(invalid)).Message).Text == "�");
    }

    private static async Task ReadTimeout()
    {
        var clock = new BleDiscoveryTestClock();
        var (receiver, native) = await Ready(clock: clock);
        native.EmitNotification(LongFrame().AsMemory(0, 173));
        await using var reader = receiver.ReceiveAsync().GetAsyncEnumerator();
        var moving = reader.MoveNextAsync().AsTask();
        var read = await native.Reads.Reader.ReadAsync();
        clock.Advance(Options.NotificationReadTimeout);
        await Throws<MeshCoreBleTransportException>(() => moving);
        Check(!receiver.IsConnected && native.DisposeCount == 0 && !receiver.CleanupCompletion.IsCompleted);
        read.Complete(LongFrame());
        await receiver.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await receiver.DisposeAsync();
        Check(native.DisposeCount == 1);
    }

    private static async Task CancelRead()
    {
        var (receiver, native) = await Ready();
        native.EmitNotification(LongFrame().AsMemory(0, 173));
        using var cancel = new CancellationTokenSource();
        await using var reader = receiver.ReceiveAsync(cancel.Token).GetAsyncEnumerator();
        var moving = reader.MoveNextAsync().AsTask();
        var read = await native.Reads.Reader.ReadAsync();
        cancel.Cancel();
        Check((await Throws<OperationCanceledException>(() => moving)).CancellationToken == cancel.Token && native.DisposeCount == 0);
        read.Complete(LongFrame());
        await receiver.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await receiver.DisposeAsync();
        Check(native.DisposeCount == 1);
    }

    private static async Task ClientDrain()
    {
        var platform = new FakeBleGattOpenPlatform();
        await using var transport = new BleFrameTransport(Options, platform, TimeProvider.System);
        await using var client = new MeshCoreClient(transport, new() { AutoReceiveMessages = false });
        var connecting = client.ConnectAsync();
        var native = new FakeBleGattSession(); native.EmitMaxPduSize(176);
        (await platform.Opens.Reader.ReadAsync()).Operation.Complete(native);
        (await native.Subscriptions.Reader.ReadAsync()).Complete(true);
        await connecting;
        var start = client.StartAsync();
        var app = await native.Writes.Reader.ReadAsync();
        var selfName = Encoding.UTF8.GetBytes("Я🐈");
        var self = new byte[58 + selfName.Length]; self[0] = (byte)PacketType.SelfInfo;
        selfName.CopyTo(self, 58);
        native.EmitNotification(self); app.Operation.Complete(true); await start;
        native.EmitMaxPduSize(160);
        await Throws<ArgumentOutOfRangeException>(() => client.SendTextAsync(new byte[32], new string('x', 145)));
        await Throws<ArgumentOutOfRangeException>(() => client.SendChannelTextAsync(0, new string('я', 76)));
        Check(!native.Writes.Reader.TryRead(out _));
        var channelSend = client.SendChannelTextAsync(0, new string('я', 75));
        var channelWrite = await native.Writes.Reader.ReadAsync();
        Check(channelWrite.Frame.Length == 157);
        native.EmitNotification(new byte[] { (byte)PacketType.Ok });
        channelWrite.Operation.Complete(true);
        await channelSend;
        native.EmitMaxPduSize(176);
        var received = new TaskCompletionSource<ContactMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += (_, e) => { if (e.Message is ContactMessage contact) received.TrySetResult(contact); };
        var drain = client.DrainMessagesAsync();
        var sync = await native.Writes.Reader.ReadAsync();
        Check(sync.Frame.Span[0] == (byte)CommandType.SyncNextMessage);
        native.EmitNotification(LongFrame().AsMemory(0, 173));
        sync.Operation.Complete(true);
        (await native.Reads.Reader.ReadAsync()).Complete(LongFrame());
        var next = await native.Writes.Reader.ReadAsync();
        Check(next.Frame.Span[0] == (byte)CommandType.SyncNextMessage);
        native.EmitNotification(new byte[] { (byte)PacketType.NoMoreMessages }); next.Operation.Complete(true);
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Check((await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Text == new string('x', 156) + "🐈");
    }
}
