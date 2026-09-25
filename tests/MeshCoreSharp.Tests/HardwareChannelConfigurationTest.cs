using System.Runtime.CompilerServices;
using System.Text;
using MeshCoreSharp;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Serial;

/// <summary>Opt-in channel lifecycle test limited to a slot confirmed empty before mutation.</summary>
internal static class HardwareChannelConfigurationTest
{
    private const string ApplicationName = "MeshCoreSharp.ChannelConfigTest";
    private const string ChannelName = "#mcs-dev-test";
    private const string Text = "Тестовая отправка при разработке MeshCoreSharp в #mcs-dev-test. Ответ не требуется";

    public static async Task RunAsync(string portName)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var guarded = new GuardedTransport(new SerialMeshCoreTransport(new SerialMeshCoreTransportOptions
        {
            PortName = portName,
            DtrEnable = true,
            RtsEnable = true,
            OpenDelay = TimeSpan.FromSeconds(2),
        }));
        await using var client = new MeshCoreClient(guarded, new MeshCoreClientOptions
        {
            ApplicationName = ApplicationName,
            ApplicationProtocolVersion = 3,
            AutoReceiveMessages = false,
        });

        client.BackgroundError += (_, args) => Console.WriteLine($"BACKGROUND ERROR: {args.Exception.Message}");
        Console.WriteLine($"CHANNEL CONFIG TEST {DateTimeOffset.Now:O}: {portName}, DTR=true, RTS=true");
        Console.WriteLine($"Exact hashtag name: {ChannelName} (no trailing whitespace). Secrets are not printed.");
        await client.ConnectAsync(stop.Token);
        try
        {
            var self = await client.StartAsync(stop.Token);
            var uptimeBefore = (await client.GetCoreStatsAsync(stop.Token)).UptimeSeconds;
            var packetsBefore = await client.GetPacketStatsAsync(stop.Token);
            var channels = await client.GetChannelsAsync(stop.Token);
            var expectedSecret = ChannelSecrets.DeriveHashtag(ChannelName);
            var existing = channels.FirstOrDefault(channel => channel.Name == ChannelName);
            if (existing is not null && !existing.Secret.Span.SequenceEqual(expectedSecret))
                throw new InvalidOperationException($"Slot {existing.Index} has the requested name with a different secret; it was not modified.");

            var empty = channels.LastOrDefault(channel => channel.IsEmpty &&
                channel.Secret.Span.IndexOfAnyExcept((byte)0) < 0 && channel.Index != existing?.Index)
                ?? throw new InvalidOperationException("No all-zero empty slot is available for the guarded lifecycle test.");
            Console.WriteLine($"Node={self.Name}, TX power={(sbyte)self.TxPowerDbm} dBm, channels={channels.Count}, " +
                $"existing={(existing is null ? "none" : existing.Index)}, empty probe={empty.Index}, uptime={uptimeBefore}s");

            guarded.AllowChannelLifecycle(empty.Index, existing is null);
            await client.SetHashtagChannelAsync(empty.Index, ChannelName, stop.Token);
            AssertChannel(await client.GetChannelAsync(empty.Index, stop.Token), expectedSecret);
            Console.WriteLine($"SET/GET OK: slot {empty.Index} contains {ChannelName}.");

            await client.ClearChannelAsync(empty.Index, stop.Token);
            var cleared = await client.GetChannelAsync(empty.Index, stop.Token);
            if (!cleared.IsEmpty || cleared.Secret.Span.IndexOfAnyExcept((byte)0) >= 0)
                throw new InvalidOperationException($"Slot {empty.Index} was not cleared completely.");
            Console.WriteLine($"CLEAR/GET OK: slot {empty.Index} is empty with an all-zero secret.");

            var target = existing;
            if (target is null)
            {
                await client.SetHashtagChannelAsync(empty.Index, ChannelName, stop.Token);
                target = await client.GetChannelAsync(empty.Index, stop.Token);
                AssertChannel(target, expectedSecret);
                Console.WriteLine($"FINAL SET/GET OK: {ChannelName} remains configured in slot {target.Index}.");
            }

            guarded.AllowSingleMessage(target.Index);
            Console.WriteLine($"Sending once ({Encoding.UTF8.GetByteCount(Text)} UTF-8 bytes): {Text}");
            var sent = await client.SendChannelTextAsync(target.Index, Text, stop.Token);
            Console.WriteLine($"ACCEPTED: channel={sent.ChannelIndex}, timestamp={sent.Timestamp}; firmware returned OK.");

            PacketStats packetsAfter = packetsBefore;
            for (var attempt = 0; attempt < 15; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
                packetsAfter = await client.GetPacketStatsAsync(stop.Token);
                if (packetsAfter.Sent != packetsBefore.Sent) break;
            }
            var uptimeAfter = (await client.GetCoreStatsAsync(stop.Token)).UptimeSeconds;
            Console.WriteLine($"TX: {packetsBefore.Sent}->{packetsAfter.Sent}; uptime: {uptimeBefore}->{uptimeAfter}s");
            if (guarded.MessageSendAttempts != 1 || packetsAfter.Sent == packetsBefore.Sent)
                throw new InvalidOperationException("Could not verify exactly one radio transmission; it was not retried.");
            if (uptimeAfter <= uptimeBefore)
                throw new InvalidOperationException("Device uptime did not increase; a reset may have occurred.");
            guarded.AssertLifecycleComplete();
            Console.WriteLine("PASS: guarded set/read/clear/read/final state and one radio TX succeeded without a reset.");
        }
        finally
        {
            await client.DisconnectAsync();
            Console.WriteLine($"CLOSE OK, connected={client.IsConnected}");
        }
    }

    private static void AssertChannel(ChannelInfo channel, ReadOnlySpan<byte> expectedSecret)
    {
        if (channel.Name != ChannelName || !channel.Secret.Span.SequenceEqual(expectedSecret))
            throw new InvalidOperationException($"Slot {channel.Index} does not match the requested hashtag channel.");
    }

    private sealed class GuardedTransport(IMeshCoreTransport inner) : IMeshCoreTransport
    {
        private static readonly byte[] AppStart = CompanionCommands.AppStart(ApplicationName, 3);
        private static readonly byte[] DeviceQuery = CompanionCommands.DeviceQuery(3);
        private static readonly byte[] CoreStats = CompanionCommands.GetStats(StatsType.Core);
        private static readonly byte[] PacketStats = CompanionCommands.GetStats(StatsType.Packets);
        private byte[][]? _lifecycle;
        private int _lifecyclePosition;
        private byte? _messageChannel;
        private int _messageSendAttempts;

        public int MessageSendAttempts => Volatile.Read(ref _messageSendAttempts);
        public bool IsConnected => inner.IsConnected;

        public void AllowChannelLifecycle(byte index, bool leaveConfigured)
        {
            var set = CompanionCommands.SetChannel(index, ChannelName, ChannelSecrets.DeriveHashtag(ChannelName));
            var clear = CompanionCommands.ClearChannel(index);
            _lifecycle = leaveConfigured ? [set, clear, set] : [set, clear];
        }

        public void AllowSingleMessage(byte channel) => _messageChannel = channel;

        public void AssertLifecycleComplete()
        {
            if (_lifecycle is null || _lifecyclePosition != _lifecycle.Length)
                throw new InvalidOperationException("The guarded channel lifecycle did not complete.");
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => inner.DisconnectAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            var span = frame.Span;
            if (span.Length == 2 && span[0] == (byte)CommandType.GetChannel)
                return inner.SendAsync(frame, cancellationToken);
            if (span.SequenceEqual(AppStart) || span.SequenceEqual(DeviceQuery) ||
                span.SequenceEqual(CoreStats) || span.SequenceEqual(PacketStats))
                return inner.SendAsync(frame, cancellationToken);
            if (_lifecycle is not null && _lifecyclePosition < _lifecycle.Length &&
                span.SequenceEqual(_lifecycle[_lifecyclePosition]))
            {
                _lifecyclePosition++;
                return inner.SendAsync(frame, cancellationToken);
            }
            if (span.Length >= 7 && span[0] == (byte)CommandType.SendChannelTextMessage &&
                span[1] == 0 && span[2] == _messageChannel && span[7..].SequenceEqual(Encoding.UTF8.GetBytes(Text)))
            {
                if (Interlocked.Increment(ref _messageSendAttempts) != 1)
                    throw new InvalidOperationException("Blocked a repeated radio send.");
                return inner.SendAsync(frame, cancellationToken);
            }
            throw new InvalidOperationException("Blocked a command outside the channel configuration test allowlist.");
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var frame in inner.ReceiveAsync(cancellationToken))
                yield return frame;
        }
    }
}
