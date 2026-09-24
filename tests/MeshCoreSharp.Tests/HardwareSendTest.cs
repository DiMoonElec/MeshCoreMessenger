using System.Runtime.CompilerServices;
using System.Text;
using MeshCoreSharp;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Serial;

/// <summary>Opt-in, single transmission to the explicitly named test channel.</summary>
internal static class HardwareSendTest
{
    private const string Text = "Тестовая отправка при разработке MeshCoreSharp. Ответ не требуется";

    public static async Task RunAsync(string portName)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var transport = new SingleSendTransport(new SerialMeshCoreTransport(new SerialMeshCoreTransportOptions
        {
            PortName = portName,
            DtrEnable = false,
            RtsEnable = false,
            OpenDelay = TimeSpan.FromSeconds(2),
        }));
        await using var client = new MeshCoreClient(transport, new MeshCoreClientOptions
        {
            ApplicationName = "MeshCoreSharp.SendTest",
            AutoReceiveMessages = false,
        });
        client.BackgroundError += (_, args) => Console.WriteLine($"BACKGROUND ERROR: {args.Exception.Message}");
        Console.WriteLine($"SINGLE CHANNEL SEND TEST {DateTimeOffset.Now:O}: {portName}");
        await client.ConnectAsync(stop.Token);
        try
        {
            var self = await client.StartAsync(stop.Token);
            var channels = await client.GetChannelsAsync(stop.Token);
            var target = channels.Single(channel => channel.Name == "#test");
            Console.WriteLine($"Node={self.Name}, channel={target.Index} ({target.Name}), TX power={(sbyte)self.TxPowerDbm} dBm");
            var before = await client.GetPacketStatsAsync(stop.Token);
            Console.WriteLine($"BEFORE: TX={before.Sent}, flood={before.SentFlood}, direct={before.SentDirect}, RX={before.Received}");
            transport.AllowChannel(target.Index);
            Console.WriteLine($"Sending once ({Encoding.UTF8.GetByteCount(Text)} UTF-8 bytes): {Text}");
            var result = await client.SendChannelTextAsync(target.Index, Text, stop.Token);
            Console.WriteLine($"ACCEPTED: channel={result.ChannelIndex}, timestamp={result.Timestamp}; firmware returned OK, no channel delivery ACK exists.");
            PacketStats after = before;
            for (var attempt = 0; attempt < 15; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
                after = await client.GetPacketStatsAsync(stop.Token);
                if (after.Sent != before.Sent) break;
            }
            Console.WriteLine($"AFTER: TX={after.Sent}, flood={after.SentFlood}, direct={after.SentDirect}, RX={after.Received}");
            if (transport.SendAttempts != 1 || after.Sent == before.Sent)
                throw new InvalidOperationException("Could not verify a radio TX increment. Do not retry automatically: the message may already have been sent.");
            Console.WriteLine("PASS: exactly one channel-send command; radio TX counter increased. Remote delivery was not confirmed.");
        }
        finally
        {
            await client.DisconnectAsync();
            Console.WriteLine($"CLOSE OK, connected={client.IsConnected}");
        }
    }

    private sealed class SingleSendTransport(IMeshCoreTransport inner) : IMeshCoreTransport
    {
        private byte? _channel;
        private int _sendAttempts;
        private static readonly byte[][] Allowed =
        [
            CompanionCommands.AppStart("MeshCoreSharp.SendTest", 3),
            CompanionCommands.DeviceQuery(3), CompanionCommands.GetStats(StatsType.Packets),
        ];
        public int SendAttempts => Volatile.Read(ref _sendAttempts);
        public void AllowChannel(byte channel) => _channel = channel;
        public bool IsConnected => inner.IsConnected;
        public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
        public Task DisconnectAsync(CancellationToken ct = default) => inner.DisconnectAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
        {
            var span = frame.Span;
            if (span.Length >= 7 && span[0] == (byte)CommandType.SendChannelTextMessage &&
                span[1] == 0 && _channel == span[2] && span[7..].SequenceEqual(Encoding.UTF8.GetBytes(Text)))
            {
                if (Interlocked.Increment(ref _sendAttempts) != 1)
                    throw new InvalidOperationException("Blocked a repeated radio send.");
            }
            else if (!(span.Length == 2 && span[0] == (byte)CommandType.GetChannel) &&
                !Allowed.Any(allowed => frame.Span.SequenceEqual(allowed)))
                throw new InvalidOperationException("Blocked a command outside the single-send test allowlist.");
            return inner.SendAsync(frame, ct);
        }
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inner.ReceiveAsync(ct)) yield return frame;
        }
    }
}
