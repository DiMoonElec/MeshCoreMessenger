using System.Diagnostics;
using System.Runtime.CompilerServices;
using MeshCoreSharp;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Serial;

internal static class HardwareReadOnlyTest
{
    public static async Task RunAsync(string portName)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var transport = new ReadOnlyTransport(new SerialMeshCoreTransport(new SerialMeshCoreTransportOptions
        {
            PortName = portName,
            DtrEnable = false,
            RtsEnable = false,
            OpenDelay = TimeSpan.FromMilliseconds(200),
        }));
        await using var client = new MeshCoreClient(transport, new MeshCoreClientOptions
        {
            ApplicationName = "MeshCoreSharp.ReadOnlyTest",
            ApplicationProtocolVersion = 3,
            AutoReceiveMessages = false,
        });
        client.BackgroundError += (_, args) => Console.WriteLine($"BACKGROUND ERROR: {args.Exception.Message}");
        client.PacketReceived += (_, args) =>
            Console.WriteLine($"USB RX {args.Packet.Type} ({args.Packet.RawFrame.Length} bytes)");
        Console.WriteLine($"READ-ONLY SERIAL TEST {DateTimeOffset.Now:O}: {portName}, 115200/8N1, DTR=false, RTS=false");
        Console.WriteLine("Allowlist: APP_START, DEVICE_QUERY, GET_DEVICE_TIME, GET_BATT_AND_STORAGE, GET_CONTACTS, GET_CHANNEL, GET_STATS(core/radio/packets).");
        Console.WriteLine("Public MeshCoreClient API; no message pump, no configuration changes, no RF-send commands.");
        var timer = Stopwatch.StartNew();
        await client.ConnectAsync(stop.Token);
        Console.WriteLine($"OPEN OK ({timer.ElapsedMilliseconds} ms)");
        try
        {
            var self = await client.StartAsync(stop.Token);
            Console.WriteLine($"SELF: name={self.Name}, TX power={(sbyte)self.TxPowerDbm} dBm, radio={self.RadioFrequencyMHz} MHz, BW={self.RadioBandwidthKHz} kHz, SF={self.SpreadingFactor}, CR={self.CodingRate}");
            var before = await client.GetPacketStatsAsync(stop.Token);
            Console.WriteLine($"RADIO BEFORE: sent={before.Sent}, flood={before.SentFlood}, direct={before.SentDirect}, received={before.Received}");
            var device = await client.GetDeviceInfoAsync(stop.Token);
            Console.WriteLine($"DEVICE: model={device.Model}, version={device.SemanticVersion}, protocol={device.FirmwareProtocolVersion}, build={device.FirmwareBuild}");
            var channels = await client.GetChannelsAsync(stop.Token);
            foreach (var channel in channels.Where(channel => !channel.IsEmpty))
                Console.WriteLine($"CHANNEL {channel.Index}: name={channel.Name}");
            Console.WriteLine($"CHANNELS: slots={channels.Count}, named={channels.Count(channel => !channel.IsEmpty)} (keys omitted)");
            var core = await client.GetCoreStatsAsync(stop.Token);
            var radio = await client.GetRadioStatsAsync(stop.Token);
            Console.WriteLine($"CORE: battery={core.BatteryMillivolts} mV, uptime={core.UptimeSeconds} s, errors=0x{core.ErrorFlags:X4}, queue={core.OutboundQueueLength}");
            Console.WriteLine($"RADIO: noise={radio.NoiseFloorDbm} dBm, RSSI={radio.LastRssiDbm} dBm, SNR={radio.LastSnrDb} dB, TX airtime={radio.TransmitAirtimeSeconds} s, RX airtime={radio.ReceiveAirtimeSeconds} s");
            for (var round = 1; round <= 3; round++)
            {
                timer.Restart();
                var time = client.GetDeviceTimeAsync(stop.Token);
                var battery = client.GetBatteryAndStorageAsync(stop.Token);
                var contacts = client.GetContactsAsync(stop.Token);
                await Task.WhenAll(time, battery, contacts);
                Console.WriteLine($"ROUND {round}: time={(await time):O}, battery={(await battery).BatteryMillivolts} mV, contacts={(await contacts).Count}, elapsed={timer.ElapsedMilliseconds} ms");
            }
            var after = await client.GetPacketStatsAsync(stop.Token);
            Console.WriteLine($"RADIO AFTER: sent={after.Sent}, flood={after.SentFlood}, direct={after.SentDirect}, received={after.Received}");
            if (before.Sent != after.Sent || before.SentFlood != after.SentFlood || before.SentDirect != after.SentDirect)
                throw new InvalidOperationException("Radio TX counters changed during the read-only test; stopping.");
            Console.WriteLine($"PASS: {transport.CommandsSent} allowlisted serial commands; radio TX counters unchanged.");
        }
        finally
        {
            timer.Restart();
            await client.DisconnectAsync();
            Console.WriteLine($"CLOSE OK ({timer.ElapsedMilliseconds} ms), connected={transport.IsConnected}");
        }
    }

    private sealed class ReadOnlyTransport(IMeshCoreTransport inner) : IMeshCoreTransport
    {
        private static readonly byte[][] Allowed =
        [
            CompanionCommands.AppStart("MeshCoreSharp.ReadOnlyTest", 3), CompanionCommands.DeviceQuery(3),
            CompanionCommands.GetDeviceTime(), CompanionCommands.GetBatteryAndStorage(), CompanionCommands.GetContacts(),
            CompanionCommands.GetStats(StatsType.Core), CompanionCommands.GetStats(StatsType.Radio),
            CompanionCommands.GetStats(StatsType.Packets),
        ];
        public int CommandsSent { get; private set; }
        public bool IsConnected => inner.IsConnected;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => inner.DisconnectAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            var getChannel = frame.Length == 2 && frame.Span[0] == (byte)CommandType.GetChannel;
            if (!getChannel && !Allowed.Any(allowed => frame.Span.SequenceEqual(allowed)))
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
