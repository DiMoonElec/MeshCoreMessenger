using System.Runtime.CompilerServices;
using MeshCoreSharp;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Serial;

internal static class HardwareAdvertisementTest
{
    public static async Task RunAsync(string portName)
    {
        var deadline = DateTimeOffset.Now.AddMinutes(10);
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var transport = new AdvertTransport(new SerialMeshCoreTransport(new SerialMeshCoreTransportOptions
        {
            PortName = portName, DtrEnable = true, RtsEnable = true, OpenDelay = TimeSpan.FromSeconds(2),
        }));
        await using var client = new MeshCoreClient(transport, new MeshCoreClientOptions
        {
            ApplicationName = "MeshCoreSharp.AdvertTest", AutoReceiveMessages = true,
        });
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<ContactMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listening = 0;
        client.PacketReceived += (_, args) =>
        {
            if (args.Packet.Type == PacketType.NoMoreMessages) drained.TrySetResult();
        };
        client.BackgroundError += (_, args) => Console.WriteLine($"BACKGROUND ERROR: {args.Exception.Message}");
        client.AdvertisementReceived += (_, args) => Console.WriteLine(
            $"ADVERT RX: key={args.Advertisement.PublicKeyHex}, new={args.Advertisement.IsNew}, name={args.Advertisement.DiscoveredContact?.Name}");
        client.MessageReceived += (_, args) =>
        {
            if (args.Message is ContactMessage message)
            {
                Console.WriteLine($"PRIVATE RX: sender={message.ContactPublicKeyPrefixHex}, time={message.Timestamp:O}, SNR={message.SnrDb}, text={System.Text.Json.JsonSerializer.Serialize(message.Text)}");
                if (Volatile.Read(ref listening) != 0) received.TrySetResult(message);
            }
            else if (args.Message is ChannelMessage channel)
                Console.WriteLine($"CHANNEL RX: index={channel.ChannelIndex}, time={channel.Timestamp:O} (text omitted)");
        };
        Console.WriteLine($"ADVERT + PRIVATE RECEIVE TEST {DateTimeOffset.Now:O}: {portName}");
        await client.ConnectAsync(stop.Token);
        try
        {
            var self = await client.StartAsync(stop.Token);
            Console.WriteLine($"NODE: {self.Name}; key={self.PublicKeyHex}; TX={(sbyte)self.TxPowerDbm} dBm");
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(15), stop.Token);
            var before = await client.GetPacketStatsAsync(stop.Token);
            Volatile.Write(ref listening, 1);
            await client.SendAdvertisementAsync(AdvertisementMode.Flood, stop.Token);
            Console.WriteLine($"ADVERT ACCEPTED: Flood; send count={transport.AdvertAttempts}. Listening for a private message until {deadline:HH:mm:ss zzz}.");
            for (var attempt = 0; attempt < 15; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
                var stats = await client.GetPacketStatsAsync(stop.Token);
                if (stats.Sent != before.Sent)
                {
                    Console.WriteLine($"RADIO TX VERIFIED: {before.Sent} -> {stats.Sent}; flood {before.SentFlood} -> {stats.SentFlood}");
                    break;
                }
                if (attempt == 14) Console.WriteLine("No TX increment observed yet; advertisement is not retried.");
            }
            var message = await received.Task.WaitAsync(stop.Token);
            Console.WriteLine($"PASS: private message delivered through MessageReceived from {message.ContactPublicKeyPrefixHex}.");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            Console.WriteLine("LISTEN TIMEOUT: no new private message confirmed before the deadline.");
        }
        finally
        {
            await client.DisconnectAsync();
            Console.WriteLine($"CLOSE OK, connected={client.IsConnected}");
        }
    }

    private sealed class AdvertTransport(IMeshCoreTransport inner) : IMeshCoreTransport
    {
        private int _attempts;
        private static readonly byte[][] Allowed =
        [
            CompanionCommands.AppStart("MeshCoreSharp.AdvertTest", 3), CompanionCommands.SyncNextMessage(),
            CompanionCommands.GetStats(StatsType.Packets),
        ];
        public int AdvertAttempts => Volatile.Read(ref _attempts);
        public bool IsConnected => inner.IsConnected;
        public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
        public Task DisconnectAsync(CancellationToken ct = default) => inner.DisconnectAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
        {
            if (frame.Span.SequenceEqual(CompanionCommands.SendAdvertisement(AdvertisementMode.Flood)))
            {
                if (Interlocked.Increment(ref _attempts) != 1) throw new InvalidOperationException("Repeated advertisement blocked.");
            }
            else if (!Allowed.Any(allowed => frame.Span.SequenceEqual(allowed)))
                throw new InvalidOperationException("Command outside advertisement/receive test allowlist.");
            return inner.SendAsync(frame, ct);
        }
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inner.ReceiveAsync(ct)) yield return frame;
        }
    }
}
