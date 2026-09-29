using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class ConversationDirectoryReaderTests
{
    [Fact]
    public async Task ProjectsChatAndEveryServiceContactTypeWithoutCreatingConversations()
    {
        await using var context = await ProjectionContext.CreateAsync();
        var contacts = new[]
        {
            Contact(1, "Chat", (int)AdvertisementType.Chat),
            Contact(2, "Repeater", (int)AdvertisementType.Repeater),
            Contact(3, "Room", (int)AdvertisementType.Room),
            Contact(4, "Sensor", (int)AdvertisementType.Sensor),
            Contact(5, "None", (int)AdvertisementType.None),
            Contact(6, "Future type", 99),
        };
        await context.ApplyDirectoryAsync(context.NodeA, context.SessionA, contacts, []);

        var chats = await context.Storage.ConversationDirectory.GetPageAsync(
            context.NodeA.Id, ConversationDirectorySection.ChatContacts, null, 20, CancellationToken);
        var services = await context.Storage.ConversationDirectory.GetPageAsync(
            context.NodeA.Id, ConversationDirectorySection.ServiceContacts, null, 20, CancellationToken);

        var chat = Assert.Single(chats.Items);
        Assert.Equal("Chat", chat.DisplayName);
        Assert.Null(chat.ConversationId);
        Assert.Equal(ConversationKind.Contact, chat.Kind);
        Assert.True(chat.PresentOnNode);
        Assert.Equal((int)AdvertisementType.Chat, chat.ContactType);
        Assert.Equal(
            [
                (int)AdvertisementType.None,
                (int)AdvertisementType.Repeater,
                (int)AdvertisementType.Room,
                (int)AdvertisementType.Sensor,
                99,
            ],
            services.Items.Select(item => item.ContactType!.Value).Order().ToArray());
        Assert.All(services.Items, item => Assert.Null(item.ConversationId));
        Assert.Null(chats.NextCursor);

        var details = await context.Storage.ConversationDirectory.GetContactDetailsAsync(
            context.NodeA.Id, contacts[0].PublicKey, CancellationToken);
        Assert.NotNull(details);
        Assert.Equal("Chat", details.DisplayName);
        Assert.Equal(64, details.OutPath?.Length);
        Assert.Null(details.AdvertPayload);
        Assert.NotNull(details.LastAdvertUtc);
        Assert.Equal(55.75, details.Latitude);
        Assert.Equal(37.62, details.Longitude);
    }

    [Fact]
    public async Task UsesJoinedNamesAndKeepsRemovedContactHistoryVisible()
    {
        await using var context = await ProjectionContext.CreateAsync();
        var contact = Contact(10, "Before", (int)AdvertisementType.Chat);
        await context.ApplyDirectoryAsync(context.NodeA, context.SessionA, [contact], []);
        var stored = await context.StoreContactAsync(context.NodeA, context.SessionA, contact.PublicKey, "hello");

        var before = Assert.Single((await context.Storage.ConversationDirectory.GetPageAsync(
            context.NodeA.Id, ConversationDirectorySection.ChatContacts, null, 10, CancellationToken)).Items);
        Assert.Equal(stored.ConversationId, before.ConversationId);
        Assert.Equal("Before", before.DisplayName);
        Assert.Equal("hello", before.LastMessageText);

        var renamed = contact with { DisplayName = "After" };
        await context.ApplyDirectoryAsync(
            context.NodeA, context.SessionA, [renamed], [], context.Now.AddMinutes(1));
        var afterRename = Assert.Single((await context.Storage.ConversationDirectory.GetPageAsync(
            context.NodeA.Id, ConversationDirectorySection.ChatContacts, null, 10, CancellationToken)).Items);
        Assert.Equal("After", afterRename.DisplayName);
        Assert.Equal(stored.ConversationId, afterRename.ConversationId);

        await context.ApplyDirectoryAsync(
            context.NodeA, context.SessionA, [], [], context.Now.AddMinutes(2));
        var removed = Assert.Single((await context.Storage.ConversationDirectory.GetPageAsync(
            context.NodeA.Id, ConversationDirectorySection.ChatContacts, null, 10, CancellationToken)).Items);
        Assert.False(removed.PresentOnNode);
        Assert.Equal(stored.ConversationId, removed.ConversationId);
        Assert.Equal("hello", removed.LastMessageText);
    }

    [Fact]
    public async Task ChannelsUseFingerprintIdentityAndAggregateActiveSlots()
    {
        await using var context = await ProjectionContext.CreateAsync();
        var sharedFingerprint = Fingerprint(20);
        var otherFingerprint = Fingerprint(30);
        var snapshot = await context.ApplyDirectoryAsync(
            context.NodeA,
            context.SessionA,
            [],
            [
                new DirectoryChannelSnapshot(1, "Same", sharedFingerprint, ChannelAccessKind.PublicOrHashtag),
                new DirectoryChannelSnapshot(2, "Same", sharedFingerprint, ChannelAccessKind.PublicOrHashtag),
                new DirectoryChannelSnapshot(3, "#looks-public", otherFingerprint, ChannelAccessKind.Unknown),
                new DirectoryChannelSnapshot(4, "Same", Fingerprint(35), ChannelAccessKind.SharedSecret),
            ]);

        var page = await context.Storage.ConversationDirectory.GetPageAsync(
            context.NodeA.Id, ConversationDirectorySection.Channels, null, 10, CancellationToken);

        Assert.Equal(3, page.Items.Count);
        var shared = page.Items.Single(item => item.Identity.SequenceEqual(sharedFingerprint));
        var other = page.Items.Single(item => item.Identity.SequenceEqual(otherFingerprint));
        Assert.Equal([1, 2], shared.ActiveChannelSlots);
        Assert.Equal(ChannelAccessKind.PublicOrHashtag, shared.ChannelAccessKind);
        Assert.Null(shared.ConversationId);
        Assert.Equal("#looks-public", other.DisplayName);
        Assert.Equal(ChannelAccessKind.Unknown, other.ChannelAccessKind);
        Assert.Contains(page.Items, item => item.ChannelAccessKind == ChannelAccessKind.SharedSecret);
        Assert.NotEqual(shared.StableKey, other.StableKey);

        var binding = snapshot.ActiveBindings.Single(item => item.Slot == 1);
        var stored = await context.StoreChannelAsync(
            context.NodeA, context.SessionA, binding, "channel message");
        var refreshed = await context.Storage.ConversationDirectory.GetPageAsync(
            context.NodeA.Id, ConversationDirectorySection.Channels, null, 10, CancellationToken);
        var withHistory = refreshed.Items.Single(item => item.Identity.SequenceEqual(sharedFingerprint));
        Assert.Equal(stored.ConversationId, withHistory.ConversationId);
        Assert.Equal("channel message", withHistory.LastMessageText);

        var details = await context.Storage.ConversationDirectory.GetChannelDetailsAsync(
            context.NodeA.Id, sharedFingerprint, CancellationToken);
        Assert.NotNull(details);
        Assert.Equal([1, 2], details.ActiveSlots);
        Assert.Equal(sharedFingerprint, details.KeyFingerprint);
        Assert.DoesNotContain(details.GetType().GetProperties(), property =>
            property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnknownHistoriesRemainSeparateAndPreserveNullTitles()
    {
        await using var context = await ProjectionContext.CreateAsync();
        var unknownContact = await context.StoreContactAsync(
            context.NodeA,
            context.SessionA,
            Key(40),
            "unknown contact");
        byte[] ambiguousPrefix = [91, 92, 93, 94, 95, 96];
        await context.ApplyDirectoryAsync(
            context.NodeA,
            context.SessionA,
            [
                Contact(ambiguousPrefix.Concat(Enumerable.Repeat((byte)1, 26)).ToArray(), "First", 1),
                Contact(ambiguousPrefix.Concat(Enumerable.Repeat((byte)2, 26)).ToArray(), "Second", 1),
            ],
            []);
        var ambiguousContact = await context.StoreContactAsync(
            context.NodeA,
            context.SessionA,
            ambiguousPrefix.Concat(Enumerable.Repeat((byte)3, 26)).ToArray(),
            "ambiguous contact");
        var unknownChannelIdentity = new byte[] { (byte)'#', 1, 2, 3, 4, 5, 6 };
        var unknownChannel = await context.StoreUnknownChannelAsync(
            context.NodeA,
            context.SessionA,
            unknownChannelIdentity,
            "unknown channel");
        await context.ExecuteAsync(
            "UPDATE Conversations SET Title = NULL WHERE Id = $id;",
            ("$id", unknownContact.ConversationId.ToString("D")));

        var contacts = await context.Storage.ConversationDirectory.GetPageAsync(
            context.NodeA.Id, ConversationDirectorySection.UnknownContacts, null, 10, CancellationToken);
        var channels = await context.Storage.ConversationDirectory.GetPageAsync(
            context.NodeA.Id, ConversationDirectorySection.UnknownChannels, null, 10, CancellationToken);

        Assert.Equal(2, contacts.Items.Count);
        var contact = contacts.Items.Single(item => item.ConversationId == unknownContact.ConversationId);
        Assert.Null(contact.DisplayName);
        Assert.Equal(ConversationKind.UnknownContact, contact.Kind);
        Assert.Null(contact.ChannelAccessKind);
        Assert.Equal(MessageResolutionState.Unresolved, contact.LastMessageResolutionState);
        var ambiguous = contacts.Items.Single(item => item.ConversationId == ambiguousContact.ConversationId);
        Assert.Equal(MessageResolutionState.Ambiguous, ambiguous.LastMessageResolutionState);
        var channel = Assert.Single(channels.Items);
        Assert.Equal(unknownChannel.ConversationId, channel.ConversationId);
        Assert.Equal(ConversationKind.UnknownChannel, channel.Kind);
        Assert.Equal(unknownChannelIdentity, channel.Identity);
        Assert.Null(channel.ChannelAccessKind);
        Assert.Empty(channel.ActiveChannelSlots);
    }

    [Fact]
    public async Task PagesAreStableBoundedAndFreshRefreshHasNoDuplicates()
    {
        await using var context = await ProjectionContext.CreateAsync();
        var contacts = Enumerable.Range(1, 7)
            .Select(index => Contact((byte)(50 + index), $"Contact {index}", (int)AdvertisementType.Chat))
            .ToArray();
        await context.ApplyDirectoryAsync(context.NodeA, context.SessionA, contacts, []);

        var all = new List<ConversationDirectoryEntry>();
        ConversationDirectoryCursor? cursor = null;
        do
        {
            var page = await context.Storage.ConversationDirectory.GetPageAsync(
                context.NodeA.Id,
                ConversationDirectorySection.ChatContacts,
                cursor,
                2,
                CancellationToken);
            Assert.InRange(page.Items.Count, 1, 2);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(7, all.Count);
        Assert.Equal(7, all.Select(item => item.StableKey).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            all.OrderByDescending(item => item.ActivitySequence)
                .ThenByDescending(item => item.ActivityUtc)
                .ThenBy(item => item.StableKey, StringComparer.Ordinal)
                .Select(item => item.StableKey),
            all.Select(item => item.StableKey));

        var renamed = contacts.Select((contact, index) => contact with
        {
            DisplayName = index == 0 ? "Renamed" : contact.DisplayName,
        }).ToArray();
        await context.ApplyDirectoryAsync(
            context.NodeA, context.SessionA, renamed, [], context.Now.AddMinutes(5));
        var refreshed = await context.Storage.ConversationDirectory.GetPageAsync(
            context.NodeA.Id, ConversationDirectorySection.ChatContacts, null, 20, CancellationToken);
        Assert.Equal(7, refreshed.Items.Count);
        Assert.Equal(7, refreshed.Items.Select(item => item.StableKey).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(refreshed.Items, item => item.DisplayName == "Renamed");
    }

    [Fact]
    public async Task EveryProjectionAndDetailsLookupIsNodeScoped()
    {
        await using var context = await ProjectionContext.CreateAsync();
        var sameKey = Key(80);
        await context.ApplyDirectoryAsync(
            context.NodeA,
            context.SessionA,
            [Contact(sameKey, "Node A", (int)AdvertisementType.Chat)],
            []);
        await context.ApplyDirectoryAsync(
            context.NodeB,
            context.SessionB,
            [Contact(sameKey, "Node B", (int)AdvertisementType.Chat)],
            []);

        var pageA = await context.Storage.ConversationDirectory.GetPageAsync(
            context.NodeA.Id, ConversationDirectorySection.ChatContacts, null, 10, CancellationToken);
        var pageB = await context.Storage.ConversationDirectory.GetPageAsync(
            context.NodeB.Id, ConversationDirectorySection.ChatContacts, null, 10, CancellationToken);

        Assert.Equal("Node A", Assert.Single(pageA.Items).DisplayName);
        Assert.Equal("Node B", Assert.Single(pageB.Items).DisplayName);
        Assert.Equal(context.NodeA.Id, Assert.Single(pageA.Items).NodeId);
        Assert.Equal(context.NodeB.Id, Assert.Single(pageB.Items).NodeId);
        Assert.Equal("Node A", (await context.Storage.ConversationDirectory.GetContactDetailsAsync(
            context.NodeA.Id, sameKey, CancellationToken))?.DisplayName);
        Assert.Null(await context.Storage.ConversationDirectory.GetContactDetailsAsync(
            Guid.NewGuid(), sameKey, CancellationToken));
    }

    [Fact]
    public async Task EmptyPagesMissingDetailsCancellationAndBoundsAreHandled()
    {
        await using var context = await ProjectionContext.CreateAsync();
        var empty = await context.Storage.ConversationDirectory.GetPageAsync(
            context.NodeA.Id, ConversationDirectorySection.Channels, null, 10, CancellationToken);
        Assert.Empty(empty.Items);
        Assert.Null(empty.NextCursor);
        Assert.Null(await context.Storage.ConversationDirectory.GetChannelDetailsAsync(
            context.NodeA.Id, Fingerprint(90), CancellationToken));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            context.Storage.ConversationDirectory.GetPageAsync(
                context.NodeA.Id, ConversationDirectorySection.Channels, null, 201, CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            context.Storage.ConversationDirectory.GetPageAsync(
                context.NodeA.Id,
                ConversationDirectorySection.Channels,
                new ConversationDirectoryCursor(
                    context.NodeA.Id,
                    ConversationDirectorySection.Channels,
                    -1,
                    context.Now,
                    "bad"),
                10,
                CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            context.Storage.ConversationDirectory.GetPageAsync(
                context.NodeA.Id,
                ConversationDirectorySection.Channels,
                new ConversationDirectoryCursor(
                    context.NodeB.Id,
                    ConversationDirectorySection.Channels,
                    0,
                    context.Now,
                    "channel:foreign"),
                10,
                CancellationToken));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Storage.ConversationDirectory.GetPageAsync(
                context.NodeA.Id,
                ConversationDirectorySection.Channels,
                null,
                10,
                cancellation.Token));
    }

    private static DirectoryContactSnapshot Contact(byte seed, string name, int type) =>
        Contact(Key(seed), name, type);

    private static DirectoryContactSnapshot Contact(byte[] key, string name, int type) => new(
        key,
        name,
        type,
        3,
        Enumerable.Repeat(key[0], 64).ToArray(),
        DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + key[0]),
        55.75,
        37.62);

    private static byte[] Key(byte seed) =>
        Enumerable.Range(0, 32).Select(index => (byte)(seed + index)).ToArray();

    private static byte[] Fingerprint(byte seed) =>
        System.Security.Cryptography.SHA256.HashData(
            Enumerable.Range(0, 16).Select(index => (byte)(seed + index)).ToArray());

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class ProjectionContext : IAsyncDisposable
    {
        private readonly string _directory;

        private ProjectionContext(
            string directory,
            LocalStorage storage,
            ConnectionProfile profile,
            NodeRecord nodeA,
            NodeRecord nodeB,
            SessionRecord sessionA,
            SessionRecord sessionB,
            DateTimeOffset now)
        {
            _directory = directory;
            Storage = storage;
            Profile = profile;
            NodeA = nodeA;
            NodeB = nodeB;
            SessionA = sessionA;
            SessionB = sessionB;
            Now = now;
            DatabasePath = Path.Combine(directory, "messenger.db");
        }

        public LocalStorage Storage { get; }
        public ConnectionProfile Profile { get; }
        public NodeRecord NodeA { get; }
        public NodeRecord NodeB { get; }
        public SessionRecord SessionA { get; }
        public SessionRecord SessionB { get; }
        public DateTimeOffset Now { get; }
        public string DatabasePath { get; }

        public static async Task<ProjectionContext> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "MeshCoreMessenger.DirectoryProjection.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var paths = new TestPaths(directory);
            var storage = await LocalStorage.OpenAsync(paths, CancellationToken);
            var now = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
            var profile = new ConnectionProfile
            {
                Id = Guid.NewGuid(),
                Name = "Test",
                Transport = ConnectionTransportKind.Tcp,
                TcpHost = "127.0.0.1",
                TcpPort = 5000,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            await storage.ConnectionProfiles.SaveAsync(profile, CancellationToken);
            var nodeA = await storage.Nodes.FindOrCreateAsync(Key(200), "A", now, CancellationToken);
            var nodeB = await storage.Nodes.FindOrCreateAsync(Key(220), "B", now, CancellationToken);
            var sessionA = new SessionRecord(Guid.NewGuid(), profile.Id, nodeA.Id, now, null, null);
            var sessionB = new SessionRecord(Guid.NewGuid(), profile.Id, nodeB.Id, now, null, null);
            await storage.Sessions.CreateAsync(sessionA, CancellationToken);
            await storage.Sessions.CreateAsync(sessionB, CancellationToken);
            return new ProjectionContext(directory, storage, profile, nodeA, nodeB, sessionA, sessionB, now);
        }

        public Task<DirectorySnapshotResult> ApplyDirectoryAsync(
            NodeRecord node,
            SessionRecord session,
            IReadOnlyList<DirectoryContactSnapshot> contacts,
            IReadOnlyList<DirectoryChannelSnapshot> channels,
            DateTimeOffset? observedUtc = null) =>
            Storage.Directories.ApplySnapshotAsync(
                node.Id,
                session.Id,
                contacts,
                channels,
                observedUtc ?? Now,
                CancellationToken);

        public Task<StoredIncomingMessage> StoreContactAsync(
            NodeRecord node,
            SessionRecord session,
            byte[] publicKey,
            string text) =>
            Storage.IncomingMessages.StoreAsync(
                Envelope(
                    node,
                    session,
                    new ContactMessage(
                        publicKey[..6],
                        1,
                        MessageTextType.Plain,
                        Now,
                        text,
                        Array.Empty<byte>(),
                        null)),
                CancellationToken);

        public Task<StoredIncomingMessage> StoreChannelAsync(
            NodeRecord node,
            SessionRecord session,
            ChannelBindingRecord binding,
            string text) =>
            Storage.IncomingMessages.StoreAsync(
                Envelope(
                    node,
                    session,
                    new ChannelMessage(binding.Slot, 1, MessageTextType.Plain, Now, text, null),
                    binding),
                CancellationToken);

        public Task<StoredIncomingMessage> StoreUnknownChannelAsync(
            NodeRecord node,
            SessionRecord session,
            byte[] identity,
            string text) =>
            Storage.IncomingMessages.StoreAsync(
                Envelope(
                    node,
                    session,
                    new ChannelMessage(9, 1, MessageTextType.Plain, Now, text, null),
                    unknownChannelIdentity: identity),
                CancellationToken);

        public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync(CancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await command.ExecuteNonQueryAsync(CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await Storage.DisposeAsync();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        private IncomingMessageEnvelope Envelope(
            NodeRecord node,
            SessionRecord session,
            ReceivedMessage message,
            ChannelBindingRecord? binding = null,
            byte[]? unknownChannelIdentity = null) =>
            new(
                Guid.NewGuid(),
                session.Id,
                node.Id,
                message,
                Now,
                binding,
                unknownChannelIdentity);
    }

    private sealed record TestPaths(string DataDirectory) : IAppPaths
    {
        public string DatabasePath => Path.Combine(DataDirectory, "messenger.db");
        public string BackupsDirectory => Path.Combine(DataDirectory, "backups");
    }
}
