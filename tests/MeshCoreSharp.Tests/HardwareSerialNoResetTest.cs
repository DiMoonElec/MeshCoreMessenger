using System.Runtime.CompilerServices;
using MeshCoreSharp;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Serial;

internal static class HardwareSerialNoResetTest
{
    private const string ApplicationName = "MeshCoreSharp.NoResetTest";

    public static async Task RunAsync(string portName)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var transport = new ProbeTransport(new SerialMeshCoreTransport(new SerialMeshCoreTransportOptions
        {
            PortName = portName,
            DtrEnable = true,
            RtsEnable = true,
            OpenDelay = TimeSpan.FromMilliseconds(200),
        }));
        await using var client = new MeshCoreClient(transport, new MeshCoreClientOptions
        {
            ApplicationName = ApplicationName,
            ApplicationProtocolVersion = 3,
            AutoReceiveMessages = false,
        });

        Console.WriteLine($"SERIAL NO-RESET TEST {DateTimeOffset.Now:O}: {portName}, DTR=true, RTS=true");
        Console.WriteLine("Allowlist: APP_START and GET_STATS(core). No queue reads, RF-send or configuration commands.");
        var first = await ProbeAsync(client, stop.Token);
        await Task.Delay(TimeSpan.FromSeconds(3), stop.Token);
        var second = await ProbeAsync(client, stop.Token);

        Console.WriteLine($"UPTIME: first={first}s, second={second}s, delta={(long)second - first}s; commands={transport.CommandsSent}");
        if (second <= first)
            throw new InvalidOperationException("Device uptime did not increase across serial reconnect; a reset may have occurred.");
        Console.WriteLine("PASS: uptime increased across close/open; no reset was observed.");
    }

    private static async Task<uint> ProbeAsync(MeshCoreClient client, CancellationToken cancellationToken)
    {
        await client.ConnectAsync(cancellationToken);
        try
        {
            var self = await client.StartAsync(cancellationToken);
            var core = await client.GetCoreStatsAsync(cancellationToken);
            Console.WriteLine($"PROBE: node={self.Name}, uptime={core.UptimeSeconds}s");
            return core.UptimeSeconds;
        }
        finally
        {
            await client.DisconnectAsync();
            Console.WriteLine($"CLOSE: connected={client.IsConnected}");
        }
    }

    private sealed class ProbeTransport(IMeshCoreTransport inner) : IMeshCoreTransport
    {
        private static readonly byte[] AppStart = CompanionCommands.AppStart(ApplicationName, 3);
        private static readonly byte[] GetCoreStats = CompanionCommands.GetStats(StatsType.Core);

        public int CommandsSent { get; private set; }
        public bool IsConnected => inner.IsConnected;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => inner.DisconnectAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            if (!frame.Span.SequenceEqual(AppStart) && !frame.Span.SequenceEqual(GetCoreStats))
                throw new InvalidOperationException("Blocked a command outside the APP_START/GET_STATS(core) allowlist.");
            CommandsSent++;
            return inner.SendAsync(frame, cancellationToken);
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var frame in inner.ReceiveAsync(cancellationToken))
                yield return frame;
        }
    }
}
