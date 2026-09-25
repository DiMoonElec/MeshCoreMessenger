using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using MeshCoreSharp;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Serial;

/// <summary>Opt-in local CRUD test using one isolated temporary contact and no RF commands.</summary>
internal static class HardwareContactMutationTest
{
    private const string ApplicationName = "MeshCoreSharp.ContactMutationTest";
    private const string InitialName = "MeshCoreSharp temp";
    private const string UpdatedName = "MeshCoreSharp temp updated";
    private static readonly byte[] TemporaryKey =
        SHA256.HashData(Encoding.UTF8.GetBytes("MeshCoreSharp temporary contact 2026-09-25"));

    public static async Task RunAsync(string portName)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var initial = Configuration(InitialName, flags: 0);
        var updated = Configuration(UpdatedName, flags: 1);
        var guarded = new GuardedTransport(new SerialMeshCoreTransport(new SerialMeshCoreTransportOptions
        {
            PortName = portName,
            DtrEnable = true,
            RtsEnable = true,
            OpenDelay = TimeSpan.FromSeconds(2),
        }), initial, updated);
        await using var client = new MeshCoreClient(guarded, new MeshCoreClientOptions
        {
            ApplicationName = ApplicationName,
            ApplicationProtocolVersion = 3,
            AutoReceiveMessages = false,
        });

        Console.WriteLine($"CONTACT MUTATION TEST {DateTimeOffset.Now:O}: {portName}, DTR=true, RTS=true");
        Console.WriteLine("Allowlist: APP_START, GET_CONTACTS, GET_STATS(core), exact temporary add/update/remove. No RF commands.");
        await client.ConnectAsync(stop.Token);
        var cleanupNeeded = false;
        try
        {
            var self = await client.StartAsync(stop.Token);
            var uptimeBefore = (await client.GetCoreStatsAsync(stop.Token)).UptimeSeconds;
            var before = await client.GetContactsAsync(stop.Token);
            if (before.Any(IsTemporary))
                throw new InvalidOperationException("The deterministic temporary key already exists; no mutation was performed.");
            Console.WriteLine($"Node={self.Name}; contacts before={before.Count}; uptime={uptimeBefore}s");

            await client.AddOrUpdateContactAsync(initial, stop.Token);
            cleanupNeeded = true;
            var afterAdd = await client.GetContactsAsync(stop.Token);
            var added = afterAdd.Single(IsTemporary);
            AssertContact(added, InitialName, 0);
            Console.WriteLine($"ADD/READ OK: contacts={afterAdd.Count}, name={added.Name}");

            await client.AddOrUpdateContactAsync(updated, stop.Token);
            var afterUpdate = await client.GetContactsAsync(stop.Token);
            var changed = afterUpdate.Single(IsTemporary);
            AssertContact(changed, UpdatedName, 1);
            Console.WriteLine($"UPDATE/READ OK: contacts={afterUpdate.Count}, name={changed.Name}, flags={changed.Flags}");

            await client.RemoveContactAsync(TemporaryKey, stop.Token);
            cleanupNeeded = false;
            await Task.Delay(TimeSpan.FromSeconds(6), stop.Token); // Allow the firmware's lazy contact-store write.
            var afterRemove = await client.GetContactsAsync(stop.Token);
            if (afterRemove.Any(IsTemporary) || afterRemove.Count != before.Count)
                throw new InvalidOperationException("Temporary contact was not removed cleanly.");
            var uptimeAfter = (await client.GetCoreStatsAsync(stop.Token)).UptimeSeconds;
            if (uptimeAfter <= uptimeBefore)
                throw new InvalidOperationException("Device uptime did not increase; a reset may have occurred.");
            guarded.AssertComplete();
            Console.WriteLine($"REMOVE/READ OK: contacts={afterRemove.Count}; uptime={uptimeBefore}->{uptimeAfter}s");
            Console.WriteLine("PASS: temporary contact add/update/remove completed without RF transmission or node reset.");
        }
        finally
        {
            if (cleanupNeeded && client.IsStarted)
            {
                try
                {
                    await client.RemoveContactAsync(TemporaryKey, CancellationToken.None);
                    Console.WriteLine("CLEANUP: temporary contact removed after an incomplete test.");
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"CLEANUP ERROR: {exception.Message}");
                }
            }
            await client.DisconnectAsync();
            Console.WriteLine($"CLOSE OK, connected={client.IsConnected}");
        }
    }

    private static ContactConfiguration Configuration(string name, byte flags) => new(
        TemporaryKey,
        AdvertisementType.Chat,
        flags,
        byte.MaxValue,
        new byte[ProtocolLimits.ContactPathSize],
        name,
        (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        0,
        0);

    private static bool IsTemporary(Contact contact) => contact.PublicKey.Span.SequenceEqual(TemporaryKey);

    private static void AssertContact(Contact contact, string name, byte flags)
    {
        if (contact.Name != name || contact.AdvertisementType != AdvertisementType.Chat ||
            contact.Flags != flags || contact.OutPathLength != byte.MaxValue ||
            contact.AdvertisementLatitude != 0 || contact.AdvertisementLongitude != 0)
            throw new InvalidOperationException("Temporary contact readback did not match the written fields.");
    }

    private sealed class GuardedTransport(
        IMeshCoreTransport inner,
        ContactConfiguration initial,
        ContactConfiguration updated) : IMeshCoreTransport
    {
        private static readonly byte[] AppStart = CompanionCommands.AppStart(ApplicationName, 3);
        private static readonly byte[] GetContacts = CompanionCommands.GetContacts();
        private static readonly byte[] GetCoreStats = CompanionCommands.GetStats(StatsType.Core);
        private readonly byte[] _add = CompanionCommands.AddOrUpdateContact(initial);
        private readonly byte[] _update = CompanionCommands.AddOrUpdateContact(updated);
        private readonly byte[] _remove = CompanionCommands.RemoveContact(TemporaryKey);
        private int _mutationStep;
        private bool _removed;

        public bool IsConnected => inner.IsConnected;

        public void AssertComplete()
        {
            if (_mutationStep != 2 || !_removed)
                throw new InvalidOperationException("The guarded contact lifecycle did not complete.");
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => inner.DisconnectAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            var span = frame.Span;
            if (span.SequenceEqual(AppStart) || span.SequenceEqual(GetContacts) || span.SequenceEqual(GetCoreStats))
                return inner.SendAsync(frame, cancellationToken);
            if (_mutationStep == 0 && span.SequenceEqual(_add))
            {
                _mutationStep = 1;
                return inner.SendAsync(frame, cancellationToken);
            }
            if (_mutationStep == 1 && span.SequenceEqual(_update))
            {
                _mutationStep = 2;
                return inner.SendAsync(frame, cancellationToken);
            }
            if (_mutationStep >= 1 && !_removed && span.SequenceEqual(_remove))
            {
                _removed = true;
                return inner.SendAsync(frame, cancellationToken);
            }
            throw new InvalidOperationException("Blocked a command outside the contact mutation test allowlist.");
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var frame in inner.ReceiveAsync(cancellationToken))
                yield return frame;
        }
    }
}
