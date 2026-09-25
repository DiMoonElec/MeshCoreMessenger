using System.Runtime.CompilerServices;
using System.Text;
using MeshCoreSharp;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Serial;

/// <summary>Opt-in test that sends exactly one private message to the contact seen in the advert test.</summary>
internal static class HardwarePrivateMessageTest
{
    private static readonly byte[] RecipientPrefix = Convert.FromHexString("DF73015A6BB9");
    private const string Text = "Тестовая личная отправка при разработке MeshCoreSharp. Ответ не требуется";

    public static async Task RunAsync(string portName)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var transport = new SinglePrivateSendTransport(new SerialMeshCoreTransport(new SerialMeshCoreTransportOptions
        {
            PortName = portName,
            DtrEnable = true,
            RtsEnable = true,
            OpenDelay = TimeSpan.FromSeconds(2),
        }));
        await using var client = new MeshCoreClient(transport, new MeshCoreClientOptions
        {
            ApplicationName = "MeshCoreSharp.PrivateSendTest",
            AutoReceiveMessages = false,
        });
        client.BackgroundError += (_, args) => Console.WriteLine($"BACKGROUND ERROR: {args.Exception.Message}");
        Console.WriteLine($"SINGLE PRIVATE SEND + ACK TEST {DateTimeOffset.Now:O}: {portName}");
        await client.ConnectAsync(stop.Token);
        try
        {
            var self = await client.StartAsync(stop.Token);
            var contacts = await client.GetContactsAsync(stop.Token);
            var matches = contacts.Where(contact =>
                contact.PublicKey.Span.StartsWith(RecipientPrefix)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(
                    $"Expected exactly one contact with prefix {Convert.ToHexString(RecipientPrefix)}, found {matches.Length}; nothing was sent.");

            var target = matches[0];
            Console.WriteLine($"NODE: {self.Name}; TX={(sbyte)self.TxPowerDbm} dBm");
            Console.WriteLine($"TARGET: name={target.Name}; key prefix={Convert.ToHexString(RecipientPrefix)}; path=0x{target.OutPathLength:X2}");
            var before = await client.GetPacketStatsAsync(stop.Token);
            Console.WriteLine($"BEFORE: TX={before.Sent}, flood={before.SentFlood}, direct={before.SentDirect}, RX={before.Received}");

            transport.AllowRecipient(target.PublicKey.Span);
            Console.WriteLine($"Sending once ({Encoding.UTF8.GetByteCount(Text)} UTF-8 bytes): {Text}");
            var sent = await client.SendTextAsync(target, Text, stop.Token);
            Console.WriteLine($"MSG_SENT: route={(sent.Accepted.IsFlood ? "flood" : "direct")}, expected ACK=0x{sent.Accepted.ExpectedAck:X8}, suggested timeout={sent.Accepted.SuggestedTimeoutMilliseconds} ms, timestamp={sent.Timestamp}");

            var delivery = await sent.Delivery;
            Console.WriteLine($"DELIVERY: {delivery.Status}" +
                (delivery.Acknowledgement is { } ack
                    ? $", ACK=0x{ack.Ack:X8}, RTT={ack.RoundTripTimeMilliseconds} ms"
                    : string.Empty));

            var after = before;
            for (var attempt = 0; attempt < 15; attempt++)
            {
                after = await client.GetPacketStatsAsync(stop.Token);
                if (after.Sent != before.Sent) break;
                await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
            }
            Console.WriteLine($"AFTER: TX={after.Sent}, flood={after.SentFlood}, direct={after.SentDirect}, RX={after.Received}");

            if (transport.SendAttempts != 1)
                throw new InvalidOperationException($"Expected one private send attempt, observed {transport.SendAttempts}.");
            if (after.Sent == before.Sent)
                throw new InvalidOperationException("Radio TX counter did not increase. The message is not retried automatically.");
            if (delivery.Status != MessageDeliveryStatus.Confirmed)
                throw new InvalidOperationException($"Private message was accepted but delivery was not confirmed ({delivery.Status}). It is not retried automatically.");

            Console.WriteLine("PASS: exactly one private-send command, radio TX incremented and matching ACK confirmed delivery.");
        }
        finally
        {
            await client.DisconnectAsync();
            Console.WriteLine($"CLOSE OK, connected={client.IsConnected}");
        }
    }

    private sealed class SinglePrivateSendTransport(IMeshCoreTransport inner) : IMeshCoreTransport
    {
        private byte[]? _recipientPrefix;
        private int _sendAttempts;
        private static readonly byte[][] Allowed =
        [
            CompanionCommands.AppStart("MeshCoreSharp.PrivateSendTest", 3),
            CompanionCommands.GetContacts(),
            CompanionCommands.GetStats(StatsType.Packets),
        ];

        public int SendAttempts => Volatile.Read(ref _sendAttempts);
        public bool IsConnected => inner.IsConnected;

        public void AllowRecipient(ReadOnlySpan<byte> publicKey)
        {
            if (publicKey.Length != ProtocolLimits.PublicKeySize || !publicKey.StartsWith(RecipientPrefix))
                throw new InvalidOperationException("Resolved contact does not match the expected sender prefix.");
            _recipientPrefix = publicKey[..ProtocolLimits.MessageContactPrefixSize].ToArray();
        }

        public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
        public Task DisconnectAsync(CancellationToken ct = default) => inner.DisconnectAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
        {
            var span = frame.Span;
            var isExpectedPrivateSend = span.Length >= 13 &&
                span[0] == (byte)CommandType.SendTextMessage && span[1] == 0 && span[2] == 0 &&
                _recipientPrefix is not null && span.Slice(7, 6).SequenceEqual(_recipientPrefix) &&
                span[13..].SequenceEqual(Encoding.UTF8.GetBytes(Text));
            if (isExpectedPrivateSend)
            {
                if (Interlocked.Increment(ref _sendAttempts) != 1)
                    throw new InvalidOperationException("Blocked a repeated private message send.");
            }
            else if (!Allowed.Any(allowed => frame.Span.SequenceEqual(allowed)))
                throw new InvalidOperationException("Blocked a command outside the single private-send test allowlist.");
            return inner.SendAsync(frame, ct);
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inner.ReceiveAsync(ct)) yield return frame;
        }
    }
}
