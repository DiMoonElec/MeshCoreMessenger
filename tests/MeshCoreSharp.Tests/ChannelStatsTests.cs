using System.Buffers.Binary;
using System.Text;
using MeshCoreSharp;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Protocol.Packets;
using MeshCoreSharp.Protocol.Parsing;

internal static class ChannelStatsTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Channel fields, empty slots, whitespace and key isolation", ChannelFields),
        ("Stats signed fields, counters, extensions and unknown subtype", StatsFields),
        ("Channel/stats truncated frames", Truncation),
        ("Channel index and stats subtype matching with interleaved push", Matching),
        ("Channel enumeration includes holes and uses device capacity", Enumeration),
        ("Channel enumeration legacy firmware, zero and maximum capacity", Capacities),
        ("Channel/stats errors and malformed frames release gate", Failures),
        ("Channel timeout, queued cancellation and enumeration cancellation", Cancellation),
    ];

    private static void Check(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new Exception(message);
    }

    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { return exception; }
        throw new Exception($"Expected {typeof(T).Name}");
    }

    private static CompanionPacket Decode(byte[] frame) => new CompanionPacketDecoder().Decode(frame);
    private static byte[] Channel(byte index = 7, string name = "  Канал🐈  ")
    {
        var frame = new byte[50];
        frame[0] = 0x12;
        frame[1] = index;
        Encoding.UTF8.GetBytes(name).CopyTo(frame.AsSpan(2, 32));
        frame.AsSpan(34, 16).Fill(0xA7);
        return frame;
    }

    private static byte[] Device(byte count)
    {
        var frame = new byte[80];
        frame[0] = 0x0D;
        frame[1] = 3;
        frame[3] = count;
        return frame;
    }

    // Fixed independent wire fixtures: 4017 mV, large uptime, flags, queue;
    // -115 dBm noise, -99 dBm RSSI, -7.25 dB SNR, 258/772 seconds airtime.
    private static byte[] Core() => [0x18, 0, 0xB1, 0x0F, 0x78, 0x56, 0x34, 0xF2, 0x01, 0x80, 9];
    private static byte[] Radio() => [0x18, 1, 0x8D, 0xFF, 0x9D, 0xE3, 2, 1, 0, 0, 4, 3, 0, 0];
    private static byte[] Packets()
    {
        var frame = new byte[30];
        frame[0] = 0x18;
        frame[1] = 2;
        for (var i = 0; i < 7; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2 + i * 4), 0xF0000000u + (uint)i);
        return frame;
    }

    private static Task ChannelFields()
    {
        var frame = Channel();
        var packet = (ChannelInfoPacket)Decode(frame);
        Check(packet.Info.Index == 7 && packet.Info.Name == "  Канал🐈  " && !packet.Info.IsEmpty);
        Check(packet.Info.Secret.Length == 16 && packet.Info.Secret.Span[0] == 0xA7);
        Check(!packet.Info.ToString().Contains("Secret") && !packet.Info.ToString().Contains("A7A7"));
        frame[34] = 0; // The decoded key owns its bytes.
        Check(packet.Info.Secret.Span[0] == 0xA7);
        Check(((ChannelInfoPacket)Decode(Channel(0, ""))).Info.IsEmpty);
        Check(!((ChannelInfoPacket)Decode(Channel(1, " "))).Info.IsEmpty);
        Check(((ChannelInfoPacket)Decode(Channel(2, "name\0ignored"))).Info.Name == "name");
        Check(((ChannelInfoPacket)Decode(Channel(255, new string('x', 32)))).Info.Name.Length == 32);
        Check(Decode([.. Channel(), 0xAA]) is ChannelInfoPacket);
        Check(CompanionCommands.GetChannel(255).SequenceEqual(new byte[] { 31, 255 }));
        return Task.CompletedTask;
    }

    private static Task StatsFields()
    {
        var core = ((CoreStatsPacket)Decode(Core())).Info;
        Check(core.BatteryMillivolts == 4017 && core.UptimeSeconds == 0xF2345678 &&
            core.ErrorFlags == 0x8001 && core.OutboundQueueLength == 9);
        var radio = ((RadioStatsPacket)Decode(Radio())).Info;
        Check(radio.NoiseFloorDbm == -115 && radio.LastRssiDbm == -99 && radio.LastSnrDb == -7.25 &&
            radio.TransmitAirtimeSeconds == 258 && radio.ReceiveAirtimeSeconds == 772);
        var packets = ((PacketStatsPacket)Decode(Packets())).Info;
        Check(packets.Received == 0xF0000000 && packets.Sent == 0xF0000001 &&
            packets.SentFlood == 0xF0000002 && packets.SentDirect == 0xF0000003 &&
            packets.ReceivedFlood == 0xF0000004 && packets.ReceivedDirect == 0xF0000005 && packets.ReceiveErrors == 0xF0000006);
        Check(Decode([.. Core(), 99]) is CoreStatsPacket);
        Check(Decode([0x18, 0xFE, 1, 2]) is RawCompanionPacket raw && raw.RawFrame.Length == 4);
        foreach (var type in Enum.GetValues<StatsType>())
            Check(CompanionCommands.GetStats(type).SequenceEqual(new byte[] { 56, (byte)type }));
        return Task.CompletedTask;
    }

    private static async Task Truncation()
    {
        foreach (var frame in new[] { Channel(), Core(), Radio(), Packets() })
            for (var length = 1; length < frame.Length; length++)
                await Throws<MeshCoreProtocolException>(() => Task.FromResult(Decode(frame[..length])));
        await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(CompanionCommands.GetStats((StatsType)255)));
    }

    private static MeshCoreClient Client(TestTransport transport, TimeSpan? timeout = null) => new(transport,
        new MeshCoreClientOptions { AutoReceiveMessages = false, CommandTimeout = timeout ?? TimeSpan.FromSeconds(2) });

    private static async Task Start(MeshCoreClient client)
    {
        await client.ConnectAsync();
        await client.StartAsync();
    }

    private static async Task<byte[]> Sent(TestTransport transport) =>
        await transport.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

    private static async Task Matching()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        var push = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PushPacketReceived += (_, _) => push.TrySetResult();
        var channel = client.GetChannelAsync(7);
        Check((await Sent(transport)).SequenceEqual(new byte[] { 31, 7 }));
        var unhandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.UnhandledPacketReceived += (_, _) => unhandled.TrySetResult();
        transport.Emit(Channel(6));
        await unhandled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!channel.IsCompleted);
        var stats = client.GetPacketStatsAsync();
        Check(!transport.Sent.Reader.TryRead(out _));
        transport.Emit([0x88, 1]);
        transport.Emit(Channel(7));
        Check((await channel).Index == 7);
        Check((await Sent(transport)).SequenceEqual(new byte[] { 56, 2 }));
        unhandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Emit(Core());
        await unhandled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!stats.IsCompleted);
        transport.Emit(Packets());
        Check((await stats).Sent == 0xF0000001);
        await push.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task Enumeration()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        var all = client.GetChannelsAsync();
        Check((await Sent(transport))[0] == 22);
        transport.Emit(Device(3));
        for (byte i = 0; i < 3; i++)
        {
            Check((await Sent(transport)).SequenceEqual(new byte[] { 31, i }));
            transport.Emit(Channel(i, i == 1 ? "" : $"channel{i}"));
        }
        var channels = await all;
        Check(channels.Count == 3 && channels[1].IsEmpty && channels[2].Name == "channel2");
        Check(!transport.Sent.Reader.TryRead(out _));
    }

    private static async Task Capacities()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        var old = client.GetChannelsAsync();
        await Sent(transport);
        transport.Emit([0x0D, 2]);
        await Throws<NotSupportedException>(() => old);
        Check(!transport.Sent.Reader.TryRead(out _));
        foreach (var count in new byte[] { 0, 255 })
        {
            // Immediate replies exercise subscribe-before-send throughout the loop.
            var calls = 0;
            transport.OnSend = (frame, _) =>
            {
                calls++;
                transport.Emit(frame.Span[0] == 22 ? Device(count) : Channel(frame.Span[1], ""));
                return ValueTask.CompletedTask;
            };
            var all = await client.GetChannelsAsync();
            Check(all.Count == count && calls == count + 1);
        }
    }

    private static async Task Failures()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        var bad = client.GetChannelAsync(255);
        await Sent(transport);
        transport.Emit([1, 2]);
        var error = await Throws<MeshCoreCommandException>(() => bad);
        Check(error.Command == CommandType.GetChannel && error.ErrorCode == MeshCoreErrorCode.NotFound);
        foreach (var stats in new[] { false, true })
        {
            Task request = stats ? client.GetCoreStatsAsync() : client.GetChannelAsync(0);
            await Sent(transport);
            transport.Emit(stats ? new byte[] { 0x18, 0, 1 } : new byte[] { 0x12, 0 });
            await Throws<MeshCoreProtocolException>(() => request);
        }
        var unsupported = client.GetPacketStatsAsync();
        await Sent(transport);
        transport.Emit([1, 1]);
        await Throws<MeshCoreCommandException>(() => unsupported);
        var recovered = client.GetRadioStatsAsync();
        Check((await Sent(transport)).SequenceEqual(new byte[] { 56, 1 }));
        transport.Emit(Radio());
        Check((await recovered).LastSnrDb == -7.25 && client.IsConnected);
    }

    private static async Task Cancellation()
    {
        var transport = new TestTransport();
        await using var client = Client(transport, TimeSpan.FromMilliseconds(150));
        await Throws<InvalidOperationException>(() => client.GetChannelsAsync());
        await Start(client);
        var blocked = client.GetChannelAsync(0);
        await Sent(transport);
        using var queuedCts = new CancellationTokenSource();
        var queued = client.GetPacketStatsAsync(queuedCts.Token);
        queuedCts.Cancel();
        await Throws<OperationCanceledException>(() => queued);
        await Throws<MeshCoreTimeoutException>(() => blocked);
        Check(!transport.Sent.Reader.TryRead(out _));
        using var allCts = new CancellationTokenSource();
        var all = client.GetChannelsAsync(allCts.Token);
        await Sent(transport);
        transport.Emit(Device(3));
        Check((await Sent(transport))[1] == 0);
        allCts.Cancel();
        await Throws<OperationCanceledException>(() => all);
        Check(!transport.Sent.Reader.TryRead(out _));
        var recovered = client.GetCoreStatsAsync();
        await Sent(transport);
        transport.Emit(Core());
        Check((await recovered).OutboundQueueLength == 9);
    }
}
