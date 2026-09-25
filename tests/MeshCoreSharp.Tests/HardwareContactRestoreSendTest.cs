using System.Runtime.CompilerServices;
using System.Text;
using MeshCoreSharp;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Serial;

/// <summary>Opt-in destructive test: remove, restore and message one explicitly identified contact.</summary>
internal static class HardwareContactRestoreSendTest
{
    private const string ApplicationName = "MeshCoreSharp.ContactRestoreSendTest";
    private const string ContactName = "RnD Mesh01";
    private const string Text = "Тестовая личная отправка при разработке MeshCoreSharp. Ответ не требуется.";
    private static readonly byte[] ContactPrefix = Convert.FromHexString("DF73015A6BB9");

    public static async Task RunAsync(string portName)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(3));
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

        Console.WriteLine($"CONTACT REMOVE/RESTORE/SEND TEST {DateTimeOffset.Now:O}: {portName}, DTR=true, RTS=true");
        Console.WriteLine($"Target must uniquely match name '{ContactName}' and key prefix {Convert.ToHexString(ContactPrefix)}.");
        await client.ConnectAsync(stop.Token);
        ContactConfiguration? snapshot = null;
        var restoreConfirmed = false;
        try
        {
            var self = await client.StartAsync(stop.Token);
            var uptimeBefore = (await client.GetCoreStatsAsync(stop.Token)).UptimeSeconds;
            var contacts = await client.GetContactsAsync(stop.Token);
            var matches = contacts.Where(contact =>
                string.Equals(contact.Name, ContactName, StringComparison.OrdinalIgnoreCase) &&
                contact.PublicKey.Span.StartsWith(ContactPrefix)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException($"Expected exactly one '{ContactName}' with the approved key prefix, found {matches.Length}; nothing was changed.");

            var original = matches[0];
            snapshot = ContactConfiguration.FromContact(original);
            guarded.Authorize(snapshot);
            var packetsBefore = await client.GetPacketStatsAsync(stop.Token);
            Console.WriteLine($"NODE: {self.Name}; target={original.Name}; path=0x{original.OutPathLength:X2}; contacts={contacts.Count}; uptime={uptimeBefore}s");

            await client.RemoveContactAsync(original, stop.Token);
            var afterRemove = await client.GetContactsAsync(stop.Token);
            if (afterRemove.Any(contact => contact.PublicKey.Span.SequenceEqual(original.PublicKey.Span)) ||
                afterRemove.Count != contacts.Count - 1)
                throw new InvalidOperationException("The target contact was not removed cleanly.");
            Console.WriteLine($"REMOVE/READ OK: contacts={afterRemove.Count}; target absent.");

            await client.AddOrUpdateContactAsync(snapshot, stop.Token);
            var afterRestore = await client.GetContactsAsync(stop.Token);
            var restored = afterRestore.Single(contact => contact.PublicKey.Span.SequenceEqual(original.PublicKey.Span));
            AssertRestored(original, restored);
            restoreConfirmed = true;
            Console.WriteLine($"ADD/READ OK: contacts={afterRestore.Count}; target restored with matching fields.");

            await Task.Delay(TimeSpan.FromSeconds(6), stop.Token); // Allow the firmware's lazy contact-store write.
            Console.WriteLine($"Sending once ({Encoding.UTF8.GetByteCount(Text)} UTF-8 bytes): {Text}");
            var sent = await client.SendTextAsync(restored, Text, stop.Token);
            Console.WriteLine($"MSG_SENT: route={(sent.Accepted.IsFlood ? "flood" : "direct")}, expected ACK=0x{sent.Accepted.ExpectedAck:X8}, suggested timeout={sent.Accepted.SuggestedTimeoutMilliseconds} ms");
            var delivery = await sent.Delivery;
            Console.WriteLine($"DELIVERY: {delivery.Status}" +
                (delivery.Acknowledgement is { } ack ? $", ACK=0x{ack.Ack:X8}, RTT={ack.RoundTripTimeMilliseconds} ms" : string.Empty));

            var packetsAfter = packetsBefore;
            for (var attempt = 0; attempt < 15; attempt++)
            {
                packetsAfter = await client.GetPacketStatsAsync(stop.Token);
                if (packetsAfter.Sent != packetsBefore.Sent) break;
                await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
            }
            var uptimeAfter = (await client.GetCoreStatsAsync(stop.Token)).UptimeSeconds;
            if (delivery.Status != MessageDeliveryStatus.Confirmed)
                throw new InvalidOperationException($"Message delivery was not confirmed ({delivery.Status}); it was not retried.");
            if (guarded.MessageSendAttempts != 1 || packetsAfter.Sent == packetsBefore.Sent)
                throw new InvalidOperationException("Could not verify exactly one radio transmission; it was not retried.");
            if (uptimeAfter <= uptimeBefore)
                throw new InvalidOperationException("Device uptime did not increase; a reset may have occurred.");
            guarded.AssertComplete();
            Console.WriteLine($"TX: {packetsBefore.Sent}->{packetsAfter.Sent}; uptime: {uptimeBefore}->{uptimeAfter}s");
            Console.WriteLine("PASS: target removed, restored from its typed snapshot, messaged once and ACK-confirmed.");
        }
        finally
        {
            if (!restoreConfirmed && snapshot is not null && client.IsStarted)
            {
                try
                {
                    await client.AddOrUpdateContactAsync(snapshot, CancellationToken.None);
                    Console.WriteLine("RECOVERY: target contact restoration command returned OK after an incomplete test.");
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"RECOVERY ERROR: {exception.Message}");
                }
            }
            await client.DisconnectAsync();
            Console.WriteLine($"CLOSE OK, connected={client.IsConnected}");
        }
    }

    private static void AssertRestored(Contact expected, Contact actual)
    {
        if (!actual.PublicKey.Span.SequenceEqual(expected.PublicKey.Span) ||
            actual.AdvertisementType != expected.AdvertisementType || actual.Flags != expected.Flags ||
            actual.OutPathLength != expected.OutPathLength || !actual.OutPath.Span.SequenceEqual(expected.OutPath.Span) ||
            actual.Name != expected.Name || actual.LastAdvertTimestamp != expected.LastAdvertTimestamp ||
            actual.AdvertisementLatitude != expected.AdvertisementLatitude ||
            actual.AdvertisementLongitude != expected.AdvertisementLongitude)
            throw new InvalidOperationException("Restored contact fields do not match the pre-removal snapshot.");
    }

    private sealed class GuardedTransport(IMeshCoreTransport inner) : IMeshCoreTransport
    {
        private static readonly byte[] AppStart = CompanionCommands.AppStart(ApplicationName, 3);
        private static readonly byte[] GetContacts = CompanionCommands.GetContacts();
        private static readonly byte[] GetCoreStats = CompanionCommands.GetStats(StatsType.Core);
        private static readonly byte[] GetPacketStats = CompanionCommands.GetStats(StatsType.Packets);
        private byte[]? _remove;
        private byte[]? _restore;
        private byte[]? _recipientPrefix;
        private int _removeAttempts;
        private int _restoreAttempts;
        private int _messageSendAttempts;

        public int MessageSendAttempts => Volatile.Read(ref _messageSendAttempts);
        public bool IsConnected => inner.IsConnected;

        public void Authorize(ContactConfiguration contact)
        {
            if (!contact.PublicKey.Span.StartsWith(ContactPrefix) ||
                !string.Equals(contact.Name, ContactName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Resolved contact does not match the approved identity.");
            _remove = CompanionCommands.RemoveContact(contact.PublicKey.Span);
            _restore = CompanionCommands.AddOrUpdateContact(contact);
            _recipientPrefix = contact.PublicKey.Span[..ProtocolLimits.MessageContactPrefixSize].ToArray();
        }

        public void AssertComplete()
        {
            if (_removeAttempts != 1 || _restoreAttempts != 1 || _messageSendAttempts != 1)
                throw new InvalidOperationException("The guarded remove/restore/send lifecycle did not complete exactly once.");
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => inner.DisconnectAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            var span = frame.Span;
            if (span.SequenceEqual(AppStart) || span.SequenceEqual(GetContacts) ||
                span.SequenceEqual(GetCoreStats) || span.SequenceEqual(GetPacketStats))
                return inner.SendAsync(frame, cancellationToken);
            if (_remove is not null && span.SequenceEqual(_remove))
            {
                if (Interlocked.Increment(ref _removeAttempts) != 1)
                    throw new InvalidOperationException("Blocked a repeated contact removal.");
                return inner.SendAsync(frame, cancellationToken);
            }
            if (_restore is not null && span.SequenceEqual(_restore))
            {
                if (Interlocked.Increment(ref _restoreAttempts) > 2)
                    throw new InvalidOperationException("Blocked more than one recovery restoration.");
                return inner.SendAsync(frame, cancellationToken);
            }
            var isExpectedMessage = span.Length >= 13 &&
                span[0] == (byte)CommandType.SendTextMessage && span[1] == 0 && span[2] == 0 &&
                _recipientPrefix is not null && span.Slice(7, 6).SequenceEqual(_recipientPrefix) &&
                span[13..].SequenceEqual(Encoding.UTF8.GetBytes(Text));
            if (isExpectedMessage)
            {
                if (Interlocked.Increment(ref _messageSendAttempts) != 1)
                    throw new InvalidOperationException("Blocked a repeated private message send.");
                return inner.SendAsync(frame, cancellationToken);
            }
            throw new InvalidOperationException("Blocked a command outside the contact restore/send test allowlist.");
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var frame in inner.ReceiveAsync(cancellationToken))
                yield return frame;
        }
    }
}
