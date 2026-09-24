using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Protocol.Packets;
using MeshCoreSharp.Protocol.Parsing;
using MeshCoreSharp.Runtime;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Serial;

internal static class HardwareReadOnlyTest
{
    private const byte PacketStatsSubtype = 2;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    public static async Task RunAsync(string portName)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var transport = new ReadOnlyTransport(new SerialMeshCoreTransport(new SerialMeshCoreTransportOptions
        {
            PortName = portName,
            DtrEnable = false,
            RtsEnable = false,
            OpenDelay = TimeSpan.FromMilliseconds(200),
        }));
        var router = new PacketRouter();
        using var dispatcher = new CommandDispatcher(transport, router);
        var decoder = new CompanionPacketDecoder();
        Console.WriteLine($"READ-ONLY SERIAL TEST {DateTimeOffset.Now:O}: {portName}, 115200/8N1, DTR=false, RTS=false");
        Console.WriteLine("Allowlist: APP_START, DEVICE_QUERY, GET_DEVICE_TIME, GET_BATT_AND_STORAGE, GET_CONTACTS, GET_STATS(packets).");
        Console.WriteLine("No message pump, no configuration changes, no RF-send commands.");
        var timer = Stopwatch.StartNew();
        await transport.ConnectAsync(stop.Token);
        Console.WriteLine($"OPEN OK ({timer.ElapsedMilliseconds} ms)");
        var receive = ReceiveAsync();
        try
        {
            var before = await StatsAsync();
            Console.WriteLine($"RADIO BEFORE: sent={before.Sent}, flood={before.SentFlood}, direct={before.SentDirect}, received={before.Received}");
            var self = await dispatcher.SendAsync<SelfInfoPacket>(CommandType.AppStart,
                CompanionCommands.AppStart("MeshCoreSharp.ReadOnlyTest", 3), "APP_START", CommandTimeout, stop.Token);
            Console.WriteLine($"SELF: name={self.Info.Name}, TX power={(sbyte)self.Info.TxPowerDbm} dBm, radio={self.Info.RadioFrequencyMHz} MHz, BW={self.Info.RadioBandwidthKHz} kHz, SF={self.Info.SpreadingFactor}, CR={self.Info.CodingRate}");
            var device = await dispatcher.SendAsync<DeviceInfoPacket>(CommandType.DeviceQuery,
                CompanionCommands.DeviceQuery(3), "DEVICE_QUERY", CommandTimeout, stop.Token);
            Console.WriteLine($"DEVICE: model={device.Info.Model}, version={device.Info.SemanticVersion}, protocol={device.Info.FirmwareProtocolVersion}, build={device.Info.FirmwareBuild}");
            for (var round = 1; round <= 3; round++)
            {
                timer.Restart();
                var time = dispatcher.SendAsync<CurrentTimePacket>(CommandType.GetDeviceTime,
                    CompanionCommands.GetDeviceTime(), "GET_DEVICE_TIME", CommandTimeout, stop.Token);
                var battery = dispatcher.SendAsync<BatteryAndStoragePacket>(CommandType.GetBatteryAndStorage,
                    CompanionCommands.GetBatteryAndStorage(), "GET_BATT_AND_STORAGE", CommandTimeout, stop.Token);
                var contacts = dispatcher.SendContactsAsync(CompanionCommands.GetContacts(),
                    CommandTimeout, TimeSpan.FromSeconds(15), stop.Token);
                await Task.WhenAll(time, battery, contacts);
                Console.WriteLine($"ROUND {round}: time={(await time).Value:O}, battery={(await battery).Info.BatteryMillivolts} mV, contacts={(await contacts).Count}, elapsed={timer.ElapsedMilliseconds} ms");
            }
            var after = await StatsAsync();
            Console.WriteLine($"RADIO AFTER: sent={after.Sent}, flood={after.SentFlood}, direct={after.SentDirect}, received={after.Received}");
            if (before.Sent != after.Sent || before.SentFlood != after.SentFlood || before.SentDirect != after.SentDirect)
                throw new InvalidOperationException("Radio TX counters changed during the read-only test; stopping.");
            Console.WriteLine($"PASS: {transport.CommandsSent} allowlisted serial commands; radio TX counters unchanged.");
        }
        finally
        {
            await stop.CancelAsync();
            try { await receive; }
            finally
            {
                timer.Restart();
                await transport.DisconnectAsync();
                Console.WriteLine($"CLOSE OK ({timer.ElapsedMilliseconds} ms), connected={transport.IsConnected}");
            }
        }

        Task<PacketStats> StatsAsync() => dispatcher.SendAsync<PacketStats>(CommandType.GetStats,
            new byte[] { (byte)CommandType.GetStats, PacketStatsSubtype }, "GET_STATS(packets)", CommandTimeout, stop.Token);

        async Task ReceiveAsync()
        {
            try
            {
                await foreach (var frame in transport.ReceiveAsync(stop.Token))
                {
                    var packet = !frame.IsEmpty && frame.Span[0] == (byte)PacketType.Stats
                        ? new PacketStats(frame) : decoder.Decode(frame);
                    router.Route(packet);
                    Console.WriteLine($"USB RX {packet.Type} ({frame.Length} bytes)");
                }
                if (!stop.IsCancellationRequested)
                    router.FailCurrent(new MeshCoreTransportException("Serial receive stream ended."));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error)
            {
                router.FailCurrent(error);
                Console.WriteLine($"RX ERROR: {error.Message}");
                throw;
            }
        }
    }

    private sealed class PacketStats : CompanionPacket
    {
        public PacketStats(ReadOnlyMemory<byte> frame) : base((byte)PacketType.Stats, frame)
        {
            if (frame.Length < 30 || frame.Span[1] != PacketStatsSubtype)
                throw new MeshCoreProtocolException("Unexpected packet stats layout.");
            Received = BinaryPrimitives.ReadUInt32LittleEndian(frame.Span[2..]);
            Sent = BinaryPrimitives.ReadUInt32LittleEndian(frame.Span[6..]);
            SentFlood = BinaryPrimitives.ReadUInt32LittleEndian(frame.Span[10..]);
            SentDirect = BinaryPrimitives.ReadUInt32LittleEndian(frame.Span[14..]);
        }
        public uint Received { get; }
        public uint Sent { get; }
        public uint SentFlood { get; }
        public uint SentDirect { get; }
    }

    private sealed class ReadOnlyTransport(IMeshCoreTransport inner) : IMeshCoreTransport
    {
        private static readonly byte[][] Allowed =
        [
            CompanionCommands.AppStart("MeshCoreSharp.ReadOnlyTest", 3), CompanionCommands.DeviceQuery(3),
            CompanionCommands.GetDeviceTime(), CompanionCommands.GetBatteryAndStorage(), CompanionCommands.GetContacts(),
            [(byte)CommandType.GetStats, PacketStatsSubtype],
        ];
        public int CommandsSent { get; private set; }
        public bool IsConnected => inner.IsConnected;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => inner.DisconnectAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            if (!Allowed.Any(allowed => frame.Span.SequenceEqual(allowed)))
                throw new InvalidOperationException("Blocked a command outside the hardware read-only allowlist.");
            CommandsSent++;
            Console.WriteLine($"USB TX {(CommandType)frame.Span[0]} ({frame.Length} bytes)");
            return inner.SendAsync(frame, cancellationToken);
        }
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var frame in inner.ReceiveAsync(cancellationToken)) yield return frame;
        }
    }
}
