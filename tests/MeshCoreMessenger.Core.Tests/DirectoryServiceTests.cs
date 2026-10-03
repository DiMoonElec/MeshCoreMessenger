using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class DirectoryServiceTests
{
    [Fact]
    public async Task AppliesCompleteSnapshotAndRepeatedSnapshotKeepsBindingsStable()
    {
        await using var context = await StorageContext.CreateAsync();
        var contacts = new[] { Contact(1, "Alice"), Contact(2, "Relay") };
        var channels = new[] { Channel(3, "#public", Secret(10), ChannelAccessKind.PublicOrHashtag) };

        var first = await context.Storage.Directories.ApplySnapshotAsync(
            context.Node.Id, context.Session.Id, contacts, channels, context.Now, CancellationToken);
        var second = await context.Storage.Directories.ApplySnapshotAsync(
            context.Node.Id, context.Session.Id, contacts, channels, context.Now.AddMinutes(1), CancellationToken);

        var resolved = await context.Storage.Directories.GetCurrentContactsByPrefixAsync(
            context.Node.Id, contacts[0].PublicKey[..6], CancellationToken);
        Assert.Equal(2, first.CurrentContacts.Count);
        Assert.Single(first.ActiveBindings);
        Assert.Empty(first.PendingChannelTransitions);
        Assert.Single(resolved);
        Assert.Single(second.ActiveBindings);
        Assert.Equal(first.ActiveBindings[0].Id, second.ActiveBindings[0].Id);
        Assert.Equal(1, second.ActiveBindings[0].Generation);
        Assert.Empty(second.PendingChannelTransitions);

        await context.Storage.Directories.ApplySnapshotAsync(
            context.Node.Id, context.Session.Id, [contacts[0]], channels, context.Now.AddMinutes(2), CancellationToken);
        var removed = await context.Storage.Directories.GetCurrentContactsByPrefixAsync(
            context.Node.Id, contacts[1].PublicKey[..6], CancellationToken);
        Assert.Empty(removed);
    }

    [Fact]
    public async Task PrefixLookupIncludesAllCurrentContactTypes()
    {
        await using var context = await StorageContext.CreateAsync();
        var prefix = new byte[] { 1, 2, 3, 4, 5, 6 };
        var first = ContactWithPrefix(prefix, tail: 7, "Chat", contactType: (int)AdvertisementType.Chat);
        var second = ContactWithPrefix(prefix, tail: 8, "Repeater", contactType: (int)AdvertisementType.Repeater);

        await context.Storage.Directories.ApplySnapshotAsync(
            context.Node.Id, context.Session.Id, [first, second], [], context.Now, CancellationToken);

        var matches = await context.Storage.Directories.GetCurrentContactsByPrefixAsync(
            context.Node.Id, prefix, CancellationToken);
        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, contact => contact.DisplayName == "Chat");
        Assert.Contains(matches, contact => contact.DisplayName == "Repeater");
    }

    [Fact]
    public async Task RenameWithSameSecretKeepsChannelAndBindingIdentity()
    {
        await using var context = await StorageContext.CreateAsync();
        var fingerprint = Fingerprint(Secret(20));
        var first = await context.Storage.Directories.ApplySnapshotAsync(
            context.Node.Id, context.Session.Id, [], [new DirectoryChannelSnapshot(1, "Before", fingerprint, ChannelAccessKind.Unknown)],
            context.Now, CancellationToken);
        var second = await context.Storage.Directories.ApplySnapshotAsync(
            context.Node.Id, context.Session.Id, [], [new DirectoryChannelSnapshot(1, "After", fingerprint, ChannelAccessKind.Unknown)],
            context.Now.AddMinutes(1), CancellationToken);

        Assert.Equal(first.ActiveBindings[0].ChannelId, second.ActiveBindings[0].ChannelId);
        Assert.Equal(first.ActiveBindings[0].Id, second.ActiveBindings[0].Id);
        Assert.Equal("After", await context.ReadSingleStringAsync("SELECT LastName FROM Channels;"));
    }

    [Fact]
    public async Task ChangedSecretCreatesPendingTransitionThenNewGeneration()
    {
        await using var context = await StorageContext.CreateAsync();
        var first = await context.Storage.Directories.ApplySnapshotAsync(
            context.Node.Id, context.Session.Id, [], [Channel(8, "Old", Secret(30), ChannelAccessKind.Unknown)],
            context.Now, CancellationToken);
        var changed = await context.Storage.Directories.ApplySnapshotAsync(
            context.Node.Id, context.Session.Id, [], [Channel(8, "New", Secret(31), ChannelAccessKind.Unknown)],
            context.Now.AddMinutes(1), CancellationToken);

        var transition = Assert.Single(changed.PendingChannelTransitions);
        Assert.Equal(ChannelTransitionKind.Rebind, transition.Kind);
        Assert.Equal(first.ActiveBindings[0].Id, transition.PreviousBinding.Id);
        var activeBeforeCommit = await context.Storage.Directories.GetActiveChannelBindingAsync(
            context.Node.Id, 8, CancellationToken);
        Assert.Equal(first.ActiveBindings[0].Id, activeBeforeCommit?.Id);

        await context.Storage.Directories.CommitPendingChannelTransitionsAsync(
            changed.PendingChannelTransitions, context.Now.AddMinutes(2), CancellationToken);

        var active = await context.Storage.Directories.GetActiveChannelBindingAsync(context.Node.Id, 8, CancellationToken);
        Assert.NotNull(active);
        Assert.Equal(2, active.Generation);
        Assert.Equal(transition.NextChannel?.Id, active.ChannelId);
        Assert.Equal(1, await context.ReadScalarIntAsync("SELECT COUNT(*) FROM ChannelBindings WHERE UnboundUtc IS NOT NULL;"));
    }

    [Fact]
    public async Task SameSecretInTwoSlotsSharesChannelButDifferentSecretsWithSameNameDoNot()
    {
        await using var context = await StorageContext.CreateAsync();
        var shared = Fingerprint(Secret(40));
        var first = await context.Storage.Directories.ApplySnapshotAsync(
            context.Node.Id,
            context.Session.Id,
            [],
            [
                new DirectoryChannelSnapshot(1, "Same", shared, ChannelAccessKind.Unknown),
                new DirectoryChannelSnapshot(2, "Same", shared, ChannelAccessKind.Unknown),
                Channel(3, "Same", Secret(41), ChannelAccessKind.Unknown),
            ],
            context.Now,
            CancellationToken);

        Assert.Equal(3, first.ActiveBindings.Count);
        Assert.Equal(first.ActiveBindings.Single(binding => binding.Slot == 1).ChannelId,
            first.ActiveBindings.Single(binding => binding.Slot == 2).ChannelId);
        Assert.NotEqual(first.ActiveBindings.Single(binding => binding.Slot == 1).ChannelId,
            first.ActiveBindings.Single(binding => binding.Slot == 3).ChannelId);
        Assert.Equal(2, await context.ReadScalarIntAsync("SELECT COUNT(*) FROM Channels;"));
    }

    [Fact]
    public async Task FailedSecondDirectoryReadPreservesPreviousSnapshot()
    {
        await using var context = await StorageContext.CreateAsync();
        var baseline = await context.Storage.Directories.ApplySnapshotAsync(
            context.Node.Id, context.Session.Id, [Contact(50, "Keep")], [], context.Now, CancellationToken);
        var client = new FakeCompanionClient(CreateSelfInfo(context.Node.PublicKey, "Node"))
        {
            Contacts = [ToMeshContact(Contact(51, "New"))],
            ChannelsError = new IOException("Channel read failed"),
        };
        var session = await context.CreateStartedSessionAsync(client);
        var service = new DirectoryService(context.Storage.Directories, TimeProvider.System);

        await Assert.ThrowsAsync<IOException>(() => service.SynchronizeAsync(session, CancellationToken));

        var existing = await context.Storage.Directories.GetCurrentContactsByPrefixAsync(
            context.Node.Id, baseline.CurrentContacts[0].PublicKeyPrefix, CancellationToken);
        Assert.Single(existing);
        Assert.Equal("Keep", existing[0].DisplayName);
        await session.StopAsync("Test completed", CancellationToken);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ServiceStoresOnlyFingerprintAndRecognizesVerifiedHashtag()
    {
        await using var context = await StorageContext.CreateAsync();
        var hashtagSecret = ChannelSecrets.DeriveHashtag("#mcs-dev-test");
        byte[] privateSecret = [0xD1, 0xA2, 0xB3, 0xC4, 0xD5, 0xE6, 0xF7, 0x18, 0x29, 0x3A, 0x4B, 0x5C, 0x6D, 0x7E, 0x8F, 0x90];
        var client = new FakeCompanionClient(CreateSelfInfo(context.Node.PublicKey, "Node"))
        {
            Contacts = [],
            Channels = [
                new ChannelInfo(1, "#mcs-dev-test", hashtagSecret),
                new ChannelInfo(2, "Imported", privateSecret),
            ],
        };
        var session = await context.CreateStartedSessionAsync(client);
        var service = new DirectoryService(context.Storage.Directories, TimeProvider.System);

        var result = await service.SynchronizeAsync(session, CancellationToken);

        Assert.Equal(ChannelAccessKind.PublicOrHashtag,
            await context.ReadChannelAccessKindAsync("#mcs-dev-test"));
        Assert.Equal(ChannelAccessKind.Unknown,
            await context.ReadChannelAccessKindAsync("Imported"));
        Assert.Equal(0, await context.ReadScalarIntAsync("SELECT COUNT(*) FROM Channels WHERE length(KeyFingerprint) != 32;"));
        Assert.False(await context.DatabaseContainsAsync(privateSecret));
        Assert.False(await context.DatabaseContainsAsync(hashtagSecret));
        Assert.Empty(result.PendingChannelTransitions);
        await session.StopAsync("Test completed", CancellationToken);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task SendReadinessRejectsOtherNodeOfflineMissingAndCollidingChatPrefixes()
    {
        await using var context = await StorageContext.CreateAsync();
        var reader = new SendReadinessReader(context.Storage.Directories, context.Storage.ConversationDirectory);
        var chat = Contact(1, "Alice");
        var connection = new ConnectionSupervisorSnapshot(ConnectionSupervisorState.Online, 1, context.Profile.Id,
            context.Session.Id, context.Node.Id, null, null) { SenderName = "Actual name" };
        var recipient = new SendRecipient(ConversationKind.Contact, chat.PublicKey);
        await context.Storage.Directories.ApplySnapshotAsync(context.Node.Id, context.Session.Id, [chat], [], context.Now, CancellationToken);
        Assert.Equal(SendReadiness.Ready, await reader.ReadAsync(connection, context.Node.Id, recipient, CancellationToken));
        Assert.Equal(SendReadiness.WrongNode, await reader.ReadAsync(connection, Guid.NewGuid(), recipient, CancellationToken));
        Assert.Equal(SendReadiness.Offline, await reader.ReadAsync(connection with { State = ConnectionSupervisorState.Offline }, context.Node.Id, recipient, CancellationToken));
        Assert.Equal(SendReadiness.Synchronizing, await reader.ReadAsync(connection with { State = ConnectionSupervisorState.Synchronizing }, context.Node.Id, recipient, CancellationToken));
        var collisionKey = chat.PublicKey.ToArray();
        collisionKey[^1]++;
        var repeater = chat with { PublicKey = collisionKey, ContactType = (int)AdvertisementType.Repeater };
        await context.Storage.Directories.ApplySnapshotAsync(context.Node.Id, context.Session.Id, [chat, repeater], [], context.Now, CancellationToken);
        Assert.Equal(SendReadiness.AmbiguousPrefix, await reader.ReadAsync(connection, context.Node.Id, recipient, CancellationToken));
        await context.Storage.Directories.ApplySnapshotAsync(context.Node.Id, context.Session.Id, [], [], context.Now, CancellationToken);
        Assert.Equal(SendReadiness.ContactMissing, await reader.ReadAsync(connection, context.Node.Id, recipient, CancellationToken));
    }

    [Fact]
    public async Task SendReadinessRequiresExplicitSlotForDuplicateFingerprintAndChecksActualBinding()
    {
        await using var context = await StorageContext.CreateAsync();
        var reader = new SendReadinessReader(context.Storage.Directories, context.Storage.ConversationDirectory);
        var channel = Channel(3, "Shared", Secret(10), ChannelAccessKind.Unknown);
        var connection = new ConnectionSupervisorSnapshot(ConnectionSupervisorState.Online, 1, context.Profile.Id,
            context.Session.Id, context.Node.Id, null, null);
        await context.Storage.Directories.ApplySnapshotAsync(context.Node.Id, context.Session.Id, [], [channel], context.Now, CancellationToken);
        var recipient = new SendRecipient(ConversationKind.Channel, channel.KeyFingerprint);
        Assert.Equal(SendReadiness.Ready, await reader.ReadAsync(connection, context.Node.Id, recipient, CancellationToken));
        await context.Storage.Directories.ApplySnapshotAsync(context.Node.Id, context.Session.Id, [], [channel, channel with { Slot = 4 }], context.Now, CancellationToken);
        Assert.Equal(SendReadiness.ChooseSlot, await reader.ReadAsync(connection, context.Node.Id, recipient, CancellationToken));
        Assert.Equal(SendReadiness.Ready, await reader.ReadAsync(connection, context.Node.Id, recipient with { ChannelSlot = 4 }, CancellationToken));
        Assert.Equal(SendReadiness.StaleBinding, await reader.ReadAsync(connection, context.Node.Id, recipient with { ChannelSlot = 7 }, CancellationToken));
        var removed = await context.Storage.Directories.ApplySnapshotAsync(context.Node.Id, context.Session.Id, [], [], context.Now, CancellationToken);
        await context.Storage.Directories.CommitPendingChannelTransitionsAsync(removed.PendingChannelTransitions, context.Now, CancellationToken);
        Assert.Equal(SendReadiness.ChannelMissing, await reader.ReadAsync(connection, context.Node.Id, recipient, CancellationToken));
    }

    private static DirectoryContactSnapshot Contact(byte seed, string name) => new(
        Key(seed), name, 1, 3, Enumerable.Repeat(seed, 64).ToArray(),
        DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + seed), 55.75, 37.62);

    private static DirectoryChannelSnapshot Channel(byte slot, string name, byte[] secret, ChannelAccessKind accessKind) =>
        new(slot, name, Fingerprint(secret), accessKind);

    private static DirectoryContactSnapshot ContactWithPrefix(byte[] prefix, byte tail, string name, int contactType)
    {
        var key = prefix.Concat(Enumerable.Repeat(tail, 26)).ToArray();
        return new DirectoryContactSnapshot(
            key, name, contactType, 0, Enumerable.Repeat(tail, 64).ToArray(),
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), 0, 0);
    }

    private static byte[] Fingerprint(byte[] secret) => System.Security.Cryptography.SHA256.HashData(secret);
    private static byte[] Secret(byte seed) => Enumerable.Range(0, 16).Select(index => (byte)(seed + index)).ToArray();
    private static byte[] Key(byte seed) => Enumerable.Range(0, 32).Select(index => (byte)(seed + index)).ToArray();
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static Contact ToMeshContact(DirectoryContactSnapshot contact) => new(
        contact.PublicKey, AdvertisementType.Chat, (byte)contact.Flags, 0xFF, contact.OutPath,
        contact.DisplayName, (uint)contact.LastAdvertUtc.ToUnixTimeSeconds(), contact.Latitude, contact.Longitude, 0);

    private static SelfInfo CreateSelfInfo(byte[] key, string name) => new(
        AdvertisementType.Chat, 1, 10, key, 0, 0, 0, 0, 0, false, 869.525, 250, 10, 5, name);

    private sealed class FakeClientFactory(FakeCompanionClient client) : IMeshCoreClientFactory
    {
        public ICompanionClient Create(ConnectionProfile profile) => client;
    }

    private sealed class FakeCompanionClient(SelfInfo selfInfo) : ICompanionClient
    {
        public SelfInfo SelfInfo { get; } = selfInfo;
        public IReadOnlyList<Contact> Contacts { get; init; } = [];
        public IReadOnlyList<ChannelInfo> Channels { get; init; } = [];
        public Exception? ChannelsError { get; init; }
        public MeshCoreConnectionState State { get; private set; } = MeshCoreConnectionState.Disconnected;
        public bool IsConnected => State == MeshCoreConnectionState.Connected;
        public bool IsStarted { get; private set; }
        public event EventHandler<MeshCoreConnectionStateChangedEventArgs>? ConnectionStateChanged
        {
            add { }
            remove { }
        }
        public event EventHandler<MeshCoreClientErrorEventArgs>? BackgroundError
        {
            add { }
            remove { }
        }
        public event EventHandler<CompanionPacketEventArgs>? PacketReceived
        {
            add { }
            remove { }
        }
        public event EventHandler<CompanionPacketEventArgs>? PushPacketReceived
        {
            add { }
            remove { }
        }
        public event EventHandler<CompanionPacketEventArgs>? UnhandledPacketReceived
        {
            add { }
            remove { }
        }
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived
        {
            add { }
            remove { }
        }
        public event EventHandler<AdvertisementReceivedEventArgs>? AdvertisementReceived
        {
            add { }
            remove { }
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = MeshCoreConnectionState.Connected;
            return Task.CompletedTask;
        }

        public Task<SelfInfo> StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsStarted = true;
            return Task.FromResult(SelfInfo);
        }

        public Task<IReadOnlyList<Contact>> GetContactsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Contacts);
        }

        public Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ChannelsError is null
                ? Task.FromResult(Channels)
                : Task.FromException<IReadOnlyList<ChannelInfo>>(ChannelsError);
        }

        public Task DrainMessagesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            State = MeshCoreConnectionState.Disconnected;
            IsStarted = false;
            return Task.CompletedTask;
        }

        public Task FlushEventsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StorageContext : IAsyncDisposable
    {
        private StorageContext(string directory, LocalStorage storage, ConnectionProfile profile, NodeRecord node, SessionRecord session)
        {
            RootDirectory = directory;
            Storage = storage;
            Profile = profile;
            Node = node;
            Session = session;
        }

        public string RootDirectory { get; }
        public LocalStorage Storage { get; }
        public ConnectionProfile Profile { get; }
        public NodeRecord Node { get; }
        public SessionRecord Session { get; }
        public DateTimeOffset Now { get; } = new(2026, 9, 25, 20, 30, 0, TimeSpan.Zero);

        public static async Task<StorageContext> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "MeshCoreMessenger.Directory.Tests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var storage = await LocalStorage.OpenAsync(new TestPaths(directory), CancellationToken);
            var now = DateTimeOffset.UtcNow;
            var profile = new ConnectionProfile
            {
                Id = Guid.NewGuid(), Name = "Test", Transport = ConnectionTransportKind.Serial,
                SerialPortName = "/dev/cu.fake", BaudRate = 115_200, OpenDelayMilliseconds = 0,
                CommandTimeoutMilliseconds = 1_000, AcknowledgementTimeoutMilliseconds = 2_000,
                CreatedUtc = now, UpdatedUtc = now,
            };
            await storage.ConnectionProfiles.SaveAsync(profile, CancellationToken);
            var node = await storage.Nodes.FindOrCreateAsync(Key(200), "Node", now, CancellationToken);
            var session = new SessionRecord(Guid.NewGuid(), profile.Id, null, now, null, null);
            await storage.Sessions.CreateAsync(session, CancellationToken);
            await storage.Sessions.BindNodeAsync(session.Id, node.Id, CancellationToken);
            return new StorageContext(directory, storage, profile, node, session with { NodeId = node.Id });
        }

        public async Task<CompanionSession> CreateStartedSessionAsync(FakeCompanionClient client)
        {
            var factory = new CompanionSessionFactory(
                new FakeClientFactory(client),
                Storage.Nodes,
                Storage.Sessions,
                new SessionCompletionTracker(Storage.Sessions),
                TimeProvider.System);
            var session = await factory.CreateAsync(Profile, generation: 1, CancellationToken);
            await session.StartAsync(CancellationToken);
            return session;
        }

        public async Task<int> ReadScalarIntAsync(string sql)
        {
            await using var connection = OpenReadOnly();
            await connection.OpenAsync(CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken));
        }

        public async Task<string> ReadSingleStringAsync(string sql)
        {
            await using var connection = OpenReadOnly();
            await connection.OpenAsync(CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (string)(await command.ExecuteScalarAsync(CancellationToken))!;
        }

        public async Task<ChannelAccessKind> ReadChannelAccessKindAsync(string name)
        {
            await using var connection = OpenReadOnly();
            await connection.OpenAsync(CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT AccessKind FROM Channels WHERE LastName = $name;";
            command.Parameters.AddWithValue("$name", name);
            return (ChannelAccessKind)Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken));
        }

        public async Task<bool> DatabaseContainsAsync(byte[] sequence)
        {
            var files = new[] { Path.Combine(RootDirectory, "messenger.db"), Path.Combine(RootDirectory, "messenger.db-wal") };
            foreach (var file in files.Where(File.Exists))
            {
                var bytes = await File.ReadAllBytesAsync(file, CancellationToken);
                if (bytes.AsSpan().IndexOf(sequence) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        public async ValueTask DisposeAsync()
        {
            await Storage.DisposeAsync();
            try { System.IO.Directory.Delete(RootDirectory, recursive: true); } catch (DirectoryNotFoundException) { }
        }

        private SqliteConnection OpenReadOnly() => new($"Data Source={Path.Combine(RootDirectory, "messenger.db")};Mode=ReadOnly");
    }

    private sealed record TestPaths(string DataDirectory) : IAppPaths
    {
        public string DatabasePath { get; } = Path.Combine(DataDirectory, "messenger.db");
        public string BackupsDirectory { get; } = Path.Combine(DataDirectory, "backups");
    }
}
