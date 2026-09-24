using System.Buffers.Binary;
using System.Text;
using MeshCoreSharp;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Protocol.Parsing;
using MeshCoreSharp.Protocol.Packets;

internal static class SendTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Text wire encoding and UTF-8 limits", EncodingAndValidation),
        ("MSG_SENT and ACK fields, truncation and extension bytes", Parsing),
        ("ACK arriving before SendAsync returns is retained", FastAck),
        ("ACK matching, duplicate ACK and command gate independence", Matching),
        ("Delivery timeout bounds and zero ACK", Timeouts),
        ("Cancellation before send and during ACK wait", Cancellation),
        ("Disconnect and receive failure terminate delivery; reconnect starts fresh", Disconnect),
        ("Send errors, malformed reply and write failure release gates", Failures),
        ("Firmware circular ACK window protects oldest pending send", Window),
        ("Channel text waits for OK and never promises delivery", ChannelSend),
        ("Duplicate expected ACK fails ambiguous deliveries", DuplicateTag),
        ("Blocked user callbacks do not block ACK completion", BlockedCallback),
        ("Contact overload uses the complete contact key", ContactOverload),
    ];

    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static void Check(bool value, string message = "Assertion failed")
    {
        if (!value) throw new Exception(message);
    }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { return exception; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static byte[] SentFrame(uint ack = 0x12345678, uint timeout = 1000, byte flood = 1)
    {
        var frame = new byte[10];
        frame[0] = 6;
        frame[1] = flood;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2), ack);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(6), timeout);
        return frame;
    }
    private static byte[] AckFrame(uint ack = 0x12345678)
    {
        var frame = new byte[9];
        frame[0] = 0x82;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), ack);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5), 123);
        return frame;
    }
    private static MeshCoreClient Client(TestTransport transport, int min = 50, int max = 2000) => new(transport,
        new MeshCoreClientOptions { AutoReceiveMessages = false, CommandTimeout = TimeSpan.FromMilliseconds(200),
            MinimumAckTimeout = TimeSpan.FromMilliseconds(min), MaximumAckTimeout = TimeSpan.FromMilliseconds(max),
            AckTimeoutMargin = TimeSpan.Zero });
    private static async Task Start(MeshCoreClient client)
    {
        await client.ConnectAsync();
        await client.StartAsync();
    }
    private static async Task<byte[]> Sent(TestTransport transport) =>
        await transport.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    private static async Task<TextMessageSendResult> Accept(MeshCoreClient client, TestTransport transport,
        uint ack = 0x12345678, uint timeout = 1000, CancellationToken ct = default)
    {
        var task = client.SendTextAsync(Key, "test", ct);
        Check((await Sent(transport))[0] == 2);
        transport.Emit(SentFrame(ack, timeout));
        return await task;
    }
    private static async Task Time(MeshCoreClient client, TestTransport transport)
    {
        var task = client.GetDeviceTimeAsync();
        Check((await Sent(transport))[0] == 5);
        transport.Emit(Fixtures.Number(PacketType.CurrentTime, 123));
        Check((await task).ToUnixTimeSeconds() == 123);
    }

    private static async Task EncodingAndValidation()
    {
        var frame = CompanionCommands.SendText(Key, "  Я🐈\n", 0xF2345678);
        Check(frame.Take(13).SequenceEqual(new byte[] { 2, 0, 0, 0x78, 0x56, 0x34, 0xF2, 0, 1, 2, 3, 4, 5 }));
        Check(Encoding.UTF8.GetString(frame.AsSpan(13)) == "  Я🐈\n");
        Check(CompanionCommands.SendText(Key, new string('я', 80), 1).Length == 173);
        await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(CompanionCommands.SendText(Key, new string('я', 81), 1)));
        foreach (var text in new[] { "", "a\0b", "\uD800" })
            await Throws<ArgumentException>(() => Task.FromResult(CompanionCommands.SendText(Key, text, 1)));
        await Throws<ArgumentException>(() => Task.FromResult(CompanionCommands.SendText(Key[..6], "test", 1)));
        var channel = CompanionCommands.SendChannelText(7, "🐈", new string('x', 154), 0x12345678);
        Check(channel.Length == 161 && channel.Take(7).SequenceEqual(new byte[] { 3, 0, 7, 0x78, 0x56, 0x34, 0x12 }));
        await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(CompanionCommands.SendChannelText(7, "🐈", new string('x', 155), 1)));
        var self = new byte[58 + 6];
        self[0] = 5;
        Encoding.UTF8.GetBytes(" Node ").CopyTo(self.AsSpan(58));
        Check(((SelfInfoPacket)new CompanionPacketDecoder().Decode(self)).Info.Name == " Node ");
        await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(new MeshCoreClient(new TestTransport(),
            new MeshCoreClientOptions { MinimumAckTimeout = TimeSpan.FromSeconds(3), MaximumAckTimeout = TimeSpan.FromSeconds(2) })));
    }

    private static async Task Parsing()
    {
        var decoder = new CompanionPacketDecoder();
        var sent = (MessageSentPacket)decoder.Decode(SentFrame(0xF2345678, uint.MaxValue));
        Check(sent.Info.IsFlood && sent.Info.ExpectedAck == 0xF2345678 && sent.Info.SuggestedTimeoutMilliseconds == uint.MaxValue);
        Check(!((MessageSentPacket)decoder.Decode(SentFrame(flood: 0))).Info.IsFlood);
        var ack = (AckPacket)decoder.Decode(AckFrame());
        Check(ack.IsPush && ack.Info.Ack == 0x12345678 && ack.Info.RoundTripTimeMilliseconds == 123);
        Check(decoder.Decode(new byte[] { 0xFA }) is RawCompanionPacket);
        foreach (var frame in new[] { SentFrame(), AckFrame() })
        {
            for (var length = 1; length < frame.Length; length++)
                await Throws<MeshCoreProtocolException>(() => Task.FromResult(decoder.Decode(frame.AsMemory(0, length))));
            byte[] extended = [.. frame, 99];
            Check(decoder.Decode(extended).RawFrame.Length == frame.Length + 1);
        }
        await Throws<MeshCoreProtocolException>(() => Task.FromResult(decoder.Decode(SentFrame(flood: 2))));
    }

    private static async Task FastAck()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        var ackSeen = Signal();
        client.PacketReceived += (_, args) => { if (args.Packet is AckPacket) ackSeen.TrySetResult(); };
        transport.OnSend = async (frame, ct) =>
        {
            transport.Emit(SentFrame());
            transport.Emit(AckFrame());
            // Keep the writer pending until RX processed both frames.
            await ackSeen.Task.WaitAsync(ct);
        };
        var sent = await client.SendTextAsync(Key, "test");
        Check((await sent.Delivery).Status == MessageDeliveryStatus.Confirmed);
    }

    private static async Task Matching()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        var first = await Accept(client, transport, 1);
        var second = await Accept(client, transport, 2);
        Check(second.Timestamp > first.Timestamp);
        transport.Emit(AckFrame(99));
        await Time(client, transport); // RX barrier and proof that ACK wait releases CommandGate.
        Check(!first.Delivery.IsCompleted && !second.Delivery.IsCompleted);
        transport.Emit(AckFrame(2));
        Check((await second.Delivery).Status == MessageDeliveryStatus.Confirmed);
        transport.Emit(AckFrame(2));
        Check(!first.Delivery.IsCompleted);
        transport.Emit(AckFrame(1));
        Check((await first.Delivery).Acknowledgement!.RoundTripTimeMilliseconds == 123);
    }

    private static async Task Timeouts()
    {
        var transport = new TestTransport();
        await using var client = Client(transport, 30, 60);
        await Start(client);
        foreach (var suggestion in new uint[] { 0, uint.MaxValue })
        {
            var result = await Accept(client, transport, timeout: suggestion);
            Check((await result.Delivery).Status == MessageDeliveryStatus.TimedOut);
            transport.Emit(AckFrame()); // A late ACK must not be retained for a future send.
            await Time(client, transport);
        }
        var noAck = await Accept(client, transport, 0);
        Check((await noAck.Delivery).Status == MessageDeliveryStatus.NotExpected);
    }

    private static async Task Cancellation()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Throws<OperationCanceledException>(() => client.SendTextAsync(Key, "test", cancelled.Token));
        Check(!transport.Sent.Reader.TryRead(out _));
        using var deliveryCts = new CancellationTokenSource();
        var sent = await Accept(client, transport, ct: deliveryCts.Token);
        deliveryCts.Cancel();
        await Throws<OperationCanceledException>(() => sent.Delivery);
        var blocking = client.GetDeviceTimeAsync();
        await Sent(transport);
        using var queuedCts = new CancellationTokenSource();
        var queued = client.SendTextAsync(Key, "queued", queuedCts.Token);
        queuedCts.Cancel();
        await Throws<OperationCanceledException>(() => queued);
        transport.Emit(Fixtures.Number(PacketType.CurrentTime, 123));
        await blocking;
        Check(!transport.Sent.Reader.TryRead(out _));
        await Time(client, transport);
    }

    private static async Task Disconnect()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        var result = await Accept(client, transport);
        await client.DisconnectAsync();
        await Throws<MeshCoreTransportException>(() => result.Delivery);
        await Start(client);
        var next = await Accept(client, transport);
        Check(!next.Delivery.IsCompleted);
        transport.Close();
        await Throws<MeshCoreTransportException>(() => next.Delivery);
    }

    private static async Task Failures()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        var error = client.SendTextAsync(Key, "test");
        await Sent(transport);
        transport.Emit([1, 2]);
        await Throws<MeshCoreCommandException>(() => error);
        var blocking = client.GetDeviceTimeAsync();
        await Sent(transport);
        var queued = client.SendChannelTextAsync(7, "must not cross connection boundaries");
        await client.DisconnectAsync();
        await Throws<MeshCoreTransportException>(() => blocking);
        await Throws<OperationCanceledException>(() => queued);
        await Start(client);
        Check(!transport.Sent.Reader.TryRead(out _));
        var malformed = client.SendTextAsync(Key, "test");
        await Sent(transport);
        transport.Emit([6, 1, 0]);
        await Throws<MeshCoreProtocolException>(() => malformed);
        var timeout = client.SendTextAsync(Key, "test");
        await Sent(transport);
        await Throws<MeshCoreTimeoutException>(() => timeout);
        transport.OnSend = (_, _) => throw new IOException("write failed");
        await Throws<IOException>(() => client.SendTextAsync(Key, "test"));
        transport.OnSend = null;
        var recovered = await Accept(client, transport);
        transport.Emit([0x82, 1]); // Malformed ACK raises a diagnostic, not a false confirmation.
        await Time(client, transport);
        Check(!recovered.Delivery.IsCompleted);
        transport.Emit(AckFrame());
        Check((await recovered.Delivery).Status == MessageDeliveryStatus.Confirmed);
    }

    private static async Task Window()
    {
        var transport = new TestTransport();
        await using var client = Client(transport, 50, 5000);
        await Start(client);
        var oldest = await Accept(client, transport, 1, 5000);
        for (uint key = 2; key <= 8; key++)
        {
            var sent = await Accept(client, transport, key, 5000);
            transport.Emit(AckFrame(key));
            await sent.Delivery;
        }
        using var cancel = new CancellationTokenSource();
        var ninth = client.SendTextAsync(Key, "ninth", cancel.Token);
        await Time(client, transport); // A text waiting for table capacity must not own CommandGate.
        Check(!ninth.IsCompleted && !transport.Sent.Reader.TryRead(out _));
        cancel.Cancel();
        await Throws<OperationCanceledException>(() => ninth);
        var replacement = client.SendTextAsync(Key, "replacement");
        transport.Emit(AckFrame(1));
        await oldest.Delivery;
        Check((await Sent(transport))[0] == 2);
        transport.Emit(SentFrame(9));
        var accepted = await replacement;
        transport.Emit(AckFrame(9));
        Check((await accepted.Delivery).Status == MessageDeliveryStatus.Confirmed);
    }

    private static async Task ChannelSend()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        var task = client.SendChannelTextAsync(7, "test");
        var frame = await Sent(transport);
        Check(frame[0] == 3 && frame[1] == 0 && frame[2] == 7 && Encoding.UTF8.GetString(frame.AsSpan(7)) == "test");
        transport.Emit(AckFrame());
        transport.Emit(SentFrame());
        transport.Emit([0]);
        Check((await task).ChannelIndex == 7);
        var error = client.SendChannelTextAsync(255, "test");
        await Sent(transport);
        transport.Emit([1, 2]);
        await Throws<MeshCoreCommandException>(() => error);
    }

    private static async Task DuplicateTag()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        var first = await Accept(client, transport);
        var second = client.SendTextAsync(Key, "test");
        await Sent(transport);
        transport.Emit(SentFrame());
        await Throws<MeshCoreProtocolException>(() => second);
        transport.Emit(AckFrame());
        await Throws<MeshCoreProtocolException>(() => first.Delivery);
    }

    private static async Task BlockedCallback()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        var entered = Signal();
        using var release = new ManualResetEventSlim();
        client.PushPacketReceived += (_, _) => { entered.TrySetResult(); release.Wait(); };
        try
        {
            transport.Emit([0x88, 1]);
            await entered.Task;
            var sent = await Accept(client, transport);
            transport.Emit(AckFrame());
            Check((await sent.Delivery).Status == MessageDeliveryStatus.Confirmed);
        }
        finally { release.Set(); }
    }

    private static async Task ContactOverload()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        var contact = ((ContactPacket)new CompanionPacketDecoder().Decode(Fixtures.Contact())).Contact;
        var sending = client.SendTextAsync(contact, "contact overload");
        var frame = await Sent(transport);
        Check(frame.AsSpan(7, 6).SequenceEqual(contact.PublicKey.Span[..6]));
        transport.Emit(SentFrame(0));
        var sent = await sending;
        Check((await sent.Delivery).Status == MessageDeliveryStatus.NotExpected);
        await Throws<ArgumentNullException>(() => client.SendTextAsync((Contact)null!, "test"));
    }
}
