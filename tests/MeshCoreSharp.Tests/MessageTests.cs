using System.Threading.Channels;
using MeshCoreSharp;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Protocol.Packets;
using MeshCoreSharp.Protocol.Parsing;
using MeshCoreSharp.Runtime;

internal static class MessageTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Message legacy/V3 fields and signed posts", ParseText),
        ("Binary channel data and truncated message headers", ParseDataAndTruncation),
        ("Message response matcher ignores unrelated frames", Matching),
        ("Startup drains all message formats and shares command gate", StartupDrain),
        ("Notifications during contacts wait for END", DuringContacts),
        ("Notification racing NO_MORE_MESSAGES is retained", FinalNotification),
        ("No sync before APP_START; automatic reception can be disabled", StartAndOptOut),
        ("Message timeout/error/malformed response release gate", FailureRecovery),
        ("Late message remains visible after a timeout", LateMessage),
        ("Cancelled APP_START does not activate message pump", CancelledStart),
        ("Disconnect cancels queued and pending message reads", Disconnect),
        ("Fresh message pump after explicit reconnect", Reconnect),
        ("Blocked message handler does not stop draining", BlockedHandler),
    ];

    private static void Check(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new Exception(message);
    }

    private static CompanionPacket Decode(byte[] frame) => new CompanionPacketDecoder().Decode(frame);
    private static ReceivedMessage Message(byte[] frame) => ((ReceivedMessagePacket)Decode(frame)).Message;

    private static Task ParseText()
    {
        foreach (var type in new[] { PacketType.ContactMessageReceived, PacketType.ContactMessageReceivedV3,
                                    PacketType.ChannelMessageReceived, PacketType.ChannelMessageReceivedV3 })
        {
            var frame = MessageFixtures.Text(type);
            var packet = (ReceivedMessagePacket)Decode(frame);
            Check(packet.Type == type && packet.RawFrame.Span.SequenceEqual(frame));
            var message = packet.Message;
            var v3 = type is PacketType.ContactMessageReceivedV3 or PacketType.ChannelMessageReceivedV3;
            Check(message.SnrDb == (v3 ? -7.25 : null) && message.PathLength == 0xFF);
            if (message is ContactMessage contact)
                Check(contact.ContactPublicKeyPrefixHex == "0123456789AB" && contact.SenderPrefix.IsEmpty &&
                    contact.Text == "  Привет!\n" && contact.TextType == MessageTextType.Plain &&
                    contact.Timestamp.ToUnixTimeSeconds() == 1_700_000_123);
            else
            {
                var channel = (ChannelMessage)message;
                Check(channel.ChannelIndex == 7 && channel.Text == "  Привет!\n" &&
                    channel.Timestamp.ToUnixTimeSeconds() == 1_700_000_123);
            }
        }
        foreach (var type in new[] { PacketType.ContactMessageReceived, PacketType.ContactMessageReceivedV3 })
        {
            var signed = (ContactMessage)Message(MessageFixtures.Text(type, "Room post", 2));
            Check(signed.TextType == MessageTextType.SignedPlain && signed.Text == "Room post" &&
                Convert.ToHexString(signed.SenderPrefix.Span) == "DEADBEEF");
            var cli = (ContactMessage)Message(MessageFixtures.Text(type, "result", 1));
            Check(cli.TextType == MessageTextType.CliData && cli.SenderPrefix.IsEmpty);
        }
        Check(((ChannelMessage)Message(MessageFixtures.Text(PacketType.ChannelMessageReceived, "Alice: hi\0padding"))).Text == "Alice: hi");
        Check(((ContactMessage)Message(MessageFixtures.Text(PacketType.ContactMessageReceived, "", 0xFE))).TextType == (MessageTextType)0xFE);
        Check(Decode([(byte)PacketType.NoMoreMessages]) is NoMoreMessagesPacket);
        Check(CompanionCommands.SyncNextMessage().SequenceEqual(new byte[] { 0x0A }));
        return Task.CompletedTask;
    }

    private static Task ParseDataAndTruncation()
    {
        var data = (ChannelDataMessage)Message(MessageFixtures.Data());
        Check(data.ChannelIndex == 7 && data.PathLength == 0x82 && data.SnrDb == -7.25 && data.DataType == 0x1234);
        Check(data.Data.Span.SequenceEqual(new byte[] { 0, 255, 10 }));
        var zero = MessageFixtures.Data()[..9];
        zero[8] = 0;
        Check(((ChannelDataMessage)Message(zero)).Data.IsEmpty);
        var frames = new[]
        {
            MessageFixtures.Text(PacketType.ContactMessageReceived, ""),
            MessageFixtures.Text(PacketType.ContactMessageReceivedV3, "", 2),
            MessageFixtures.Text(PacketType.ChannelMessageReceived, ""),
            MessageFixtures.Text(PacketType.ChannelMessageReceivedV3, ""),
            MessageFixtures.Data(),
        };
        foreach (var frame in frames)
            for (var n = 1; n < frame.Length; n++)
            {
                try { Decode(frame[..n]); }
                catch (MeshCoreProtocolException) { continue; }
                throw new Exception($"Truncated message accepted: type {frame[0]}, length {n}");
            }
        return Task.CompletedTask;
    }

    private static async Task Matching()
    {
        await using var transport = new TestTransport();
        var router = new PacketRouter();
        using var dispatcher = new CommandDispatcher(transport, router);
        transport.OnSend = (command, _) =>
        {
            Check(command.Span.SequenceEqual(new byte[] { 0x0A }));
            Check(!router.Route(Decode([(byte)PacketType.MessagesWaiting])));
            Check(!router.Route(Decode(Fixtures.Number(PacketType.CurrentTime, 42))));
            Check(!router.Route(Decode([(byte)PacketType.Ok])));
            Check(router.Route(Decode(MessageFixtures.Data())));
            return ValueTask.CompletedTask;
        };
        Check(await dispatcher.SyncNextMessageAsync(TimeSpan.FromSeconds(1), default) is ReceivedMessagePacket);
    }

    private static TestTransport Transport() => new() { AutoReplyToMessageSync = false };
    private static async Task<MeshCoreClient> Ready(TestTransport transport, MeshCoreClientOptions? options = null)
    {
        var client = new MeshCoreClient(transport, options);
        await client.ConnectAsync();
        await client.StartAsync();
        return client;
    }

    private static async Task Expect(TestTransport transport, CommandType type)
    {
        var frame = await transport.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Check(frame.SequenceEqual(new[] { (byte)type }), $"Expected {type}, got {Convert.ToHexString(frame)}");
    }

    private static async Task Time(TestTransport transport, MeshCoreClient client)
    {
        var task = client.GetDeviceTimeAsync();
        await Expect(transport, CommandType.GetDeviceTime);
        transport.Emit(Fixtures.Number(PacketType.CurrentTime, 42));
        Check((await task).ToUnixTimeSeconds() == 42);
    }

    private static async Task StartupDrain()
    {
        await using var transport = Transport();
        await using var client = await Ready(transport);
        var delivered = Channel.CreateUnbounded<ReceivedMessage>();
        var pushes = Channel.CreateUnbounded<PacketType>();
        client.MessageReceived += (_, e) => delivered.Writer.TryWrite(e.Message);
        client.PushPacketReceived += (_, e) => pushes.Writer.TryWrite(e.Packet.Type);
        await Expect(transport, CommandType.SyncNextMessage); // No tickle: read offline backlog at startup.
        var time = client.GetDeviceTimeAsync(); // Must wait until this one message response arrives.
        for (var n = 0; n < 100; n++) transport.Emit([(byte)PacketType.MessagesWaiting]);
        transport.Emit(Fixtures.Advertisement(0xAB));
        for (var n = 0; n < 101; n++) await pushes.Reader.ReadAsync();
        Check(!transport.Sent.Reader.TryRead(out _), "Parallel sync/normal command while waiting for a response");
        var frames = new[]
        {
            MessageFixtures.Text(PacketType.ContactMessageReceived),
            MessageFixtures.Text(PacketType.ChannelMessageReceived),
            MessageFixtures.Text(PacketType.ContactMessageReceivedV3, "room", 2),
            MessageFixtures.Text(PacketType.ChannelMessageReceivedV3),
            MessageFixtures.Data(),
        };
        transport.Emit(frames[0]);
        await Expect(transport, CommandType.GetDeviceTime);
        transport.Emit(Fixtures.Number(PacketType.CurrentTime, 42));
        await time;
        for (var n = 1; n < frames.Length; n++)
        {
            await Expect(transport, CommandType.SyncNextMessage);
            transport.Emit(frames[n]);
        }
        await Expect(transport, CommandType.SyncNextMessage);
        transport.Emit([(byte)PacketType.NoMoreMessages]);
        for (var n = 0; n < frames.Length; n++)
            Check((await delivered.Reader.ReadAsync()).GetType() == Message(frames[n]).GetType());
        await Time(transport, client); // Barrier: final sync released its gate.
        Check(!delivered.Reader.TryRead(out _) && !transport.Sent.Reader.TryRead(out _));
    }

    private static async Task DuringContacts()
    {
        await using var transport = Transport();
        await using var client = await Ready(transport);
        await Expect(transport, CommandType.SyncNextMessage);
        transport.Emit([(byte)PacketType.NoMoreMessages]);
        var contacts = client.GetContactsAsync();
        await Expect(transport, CommandType.GetContacts);
        transport.Emit(Fixtures.Start(1));
        transport.Emit(Fixtures.Contact());
        var tickle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PushPacketReceived += (_, _) => tickle.TrySetResult();
        transport.Emit([(byte)PacketType.MessagesWaiting]);
        await tickle.Task;
        Check(!transport.Sent.Reader.TryRead(out _));
        transport.Emit(Fixtures.End());
        Check((await contacts).Count == 1);
        await Expect(transport, CommandType.SyncNextMessage);
        transport.Emit([(byte)PacketType.NoMoreMessages]);
        await Time(transport, client);
    }

    private static async Task FinalNotification()
    {
        await using var transport = Transport();
        await using var client = await Ready(transport);
        await Expect(transport, CommandType.SyncNextMessage);
        // The device may notify about a new arrival while an earlier empty response is in flight.
        transport.Emit([(byte)PacketType.MessagesWaiting]);
        transport.Emit([(byte)PacketType.NoMoreMessages]);
        await Expect(transport, CommandType.SyncNextMessage);
        transport.Emit(MessageFixtures.Data());
        await Expect(transport, CommandType.SyncNextMessage);
        transport.Emit([(byte)PacketType.NoMoreMessages]);
        await Time(transport, client);
        Check(!transport.Sent.Reader.TryRead(out _));
    }

    private static async Task StartAndOptOut()
    {
        foreach (var enabled in new[] { true, false })
        {
            await using var transport = Transport();
            await using var client = new MeshCoreClient(transport, new() { AutoReceiveMessages = enabled });
            var pushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.PushPacketReceived += (_, _) => pushed.TrySetResult();
            await client.ConnectAsync();
            transport.Emit([(byte)PacketType.MessagesWaiting]);
            await pushed.Task;
            Check(!transport.Sent.Reader.TryRead(out _));
            await client.StartAsync();
            if (enabled)
            {
                await Expect(transport, CommandType.SyncNextMessage);
                transport.Emit([(byte)PacketType.NoMoreMessages]);
            }
            await Time(transport, client);
            Check(!transport.Sent.Reader.TryRead(out _));
        }
    }

    private static async Task FailureRecovery()
    {
        foreach (var failure in new[] { "timeout", "error", "malformed" })
        {
            await using var transport = Transport();
            await using var client = await Ready(transport, new() { CommandTimeout = TimeSpan.FromMilliseconds(150) });
            var errors = Channel.CreateUnbounded<Exception>();
            client.BackgroundError += (_, e) => errors.Writer.TryWrite(e.Exception);
            await Expect(transport, CommandType.SyncNextMessage);
            if (failure == "error") transport.Emit([(byte)PacketType.Error, (byte)MeshCoreErrorCode.BadState]);
            if (failure == "malformed") transport.Emit([0x10, 0xE3]);
            var error = await errors.Reader.ReadAsync();
            Check(failure switch
            {
                "timeout" => error is MeshCoreTimeoutException,
                "error" => error is MeshCoreCommandException,
                _ => error is MeshCoreProtocolException,
            });
            Check(client.IsConnected);
            await Time(transport, client);
            // No automatic retry loop. A later tickle resumes draining.
            transport.Emit([(byte)PacketType.MessagesWaiting]);
            await Expect(transport, CommandType.SyncNextMessage);
            transport.Emit([(byte)PacketType.NoMoreMessages]);
            await Time(transport, client);
            Check(!errors.Reader.TryRead(out _));
        }
    }

    private static async Task Disconnect()
    {
        foreach (var queued in new[] { true, false })
        {
            await using var transport = Transport();
            await using var client = await Ready(transport);
            await Expect(transport, CommandType.SyncNextMessage);
            Task? contacts = null;
            if (queued)
            {
                transport.Emit([(byte)PacketType.NoMoreMessages]);
                contacts = client.GetContactsAsync();
                await Expect(transport, CommandType.GetContacts);
                transport.Emit([(byte)PacketType.MessagesWaiting]);
            }
            await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
            if (contacts is not null)
            {
                try { await contacts; throw new Exception("Expected interrupted contacts"); }
                catch (MeshCoreTransportException) { }
            }
            Check(!transport.Sent.Reader.TryRead(out _));
        }
    }

    private static async Task LateMessage()
    {
        await using var transport = Transport();
        await using var client = await Ready(transport, new() { CommandTimeout = TimeSpan.FromMilliseconds(150) });
        var timeout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BackgroundError += (_, e) => { if (e.Exception is MeshCoreTimeoutException) timeout.TrySetResult(); };
        client.MessageReceived += (_, e) => received.TrySetResult(e.Message);
        await Expect(transport, CommandType.SyncNextMessage);
        await timeout.Task;
        var time = client.GetDeviceTimeAsync();
        await Expect(transport, CommandType.GetDeviceTime);
        transport.Emit(MessageFixtures.Data());
        Check(await received.Task is ChannelDataMessage && !time.IsCompleted);
        transport.Emit(Fixtures.Number(PacketType.CurrentTime, 42));
        await time;
    }

    private static async Task CancelledStart()
    {
        await using var transport = Transport();
        await using var client = new MeshCoreClient(transport);
        await client.ConnectAsync();
        transport.OnSend = (_, _) => ValueTask.CompletedTask; // APP_START receives no reply.
        using var cts = new CancellationTokenSource();
        var start = client.StartAsync(cts.Token);
        cts.Cancel();
        try { await start; throw new Exception("Expected cancelled APP_START"); }
        catch (OperationCanceledException) { }
        Check(!client.IsStarted && !transport.Sent.Reader.TryRead(out _));
        transport.OnSend = null;
        await client.StartAsync();
        await Expect(transport, CommandType.SyncNextMessage);
        transport.Emit([(byte)PacketType.NoMoreMessages]);
        await Time(transport, client);
    }

    private static async Task Reconnect()
    {
        await using var transport = Transport();
        await using var client = await Ready(transport);
        await Expect(transport, CommandType.SyncNextMessage);
        var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += (_, e) => { if (e.CurrentState == MeshCoreConnectionState.Faulted) fault.TrySetResult(); };
        transport.Close();
        await fault.Task;
        Check(!client.IsStarted);
        await client.ConnectAsync();
        await client.StartAsync();
        await Expect(transport, CommandType.SyncNextMessage);
        transport.Emit([(byte)PacketType.NoMoreMessages]);
        await Time(transport, client);
    }

    private static async Task BlockedHandler()
    {
        await using var transport = Transport();
        await using var client = await Ready(transport);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var left = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += (_, _) => throw new Exception("Consumer error");
        client.MessageReceived += (_, _) => { entered.TrySetResult(); release.Wait(); left.TrySetResult(); };
        try
        {
            await Expect(transport, CommandType.SyncNextMessage);
            transport.Emit(MessageFixtures.Data());
            await entered.Task;
            await Expect(transport, CommandType.SyncNextMessage);
            transport.Emit([(byte)PacketType.NoMoreMessages]);
            await Time(transport, client);
        }
        finally
        {
            release.Set();
            if (entered.Task.IsCompleted) await left.Task;
        }
    }
}
