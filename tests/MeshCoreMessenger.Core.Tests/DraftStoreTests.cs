using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class DraftStoreTests
{
    [Fact]
    public async Task KnownTargetsCreateConversationsPersistExactTextAndDeleteWithoutSchemaChanges()
    {
        await using var context = await DraftContext.CreateAsync();
        var contactTarget = context.ContactTarget();
        var channelTarget = context.ChannelTarget();

        Assert.Null(await context.Storage.Drafts.GetAsync(contactTarget, CancellationToken));
        var contact = await context.Storage.Drafts.SaveAsync(
            contactTarget, "  Привет 👋\n", context.Now, CancellationToken);
        var channel = await context.Storage.Drafts.SaveAsync(
            channelTarget, "#черновик", context.Now.AddSeconds(1), CancellationToken);

        Assert.NotNull(contact);
        Assert.NotNull(channel);
        Assert.Equal("  Привет 👋\n", (await context.Storage.Drafts.GetAsync(
            contactTarget, CancellationToken))?.Text);
        Assert.Equal("#черновик", (await context.Storage.Drafts.GetAsync(
            channelTarget, CancellationToken))?.Text);
        Assert.Equal(2, (await context.Storage.History.GetConversationsAsync(
            context.Node.Id, 10, CancellationToken)).Count);

        await context.ReopenAsync();
        Assert.Equal("  Привет 👋\n", (await context.Storage.Drafts.GetAsync(
            contactTarget, CancellationToken))?.Text);
        Assert.Equal("#черновик", (await context.Storage.Drafts.GetAsync(
            channelTarget, CancellationToken))?.Text);

        Assert.Null(await context.Storage.Drafts.SaveAsync(
            contactTarget, string.Empty, context.Now.AddSeconds(2), CancellationToken));
        Assert.Null(await context.Storage.Drafts.GetAsync(contactTarget, CancellationToken));
    }

    [Fact]
    public async Task UnknownAndForeignConversationTargetsAreRejected()
    {
        await using var context = await DraftContext.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => context.Storage.Drafts.SaveAsync(
            new DraftTarget(context.Node.Id, null, ConversationKind.UnknownContact, context.ContactKey),
            "not allowed",
            context.Now,
            CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => context.Storage.Drafts.SaveAsync(
            context.ContactTarget() with { ConversationId = Guid.NewGuid() },
            "wrong owner",
            context.Now,
            CancellationToken));
    }

    [Fact]
    public async Task DraftAndIncomingIngestConcurrentlyResolveOneConversation()
    {
        await using var context = await DraftContext.CreateAsync();
        var save = context.Storage.Drafts.SaveAsync(
            context.ContactTarget(), "draft", context.Now, CancellationToken);
        var ingest = context.Storage.IncomingMessages.StoreAsync(
            new IncomingMessageEnvelope(
                Guid.NewGuid(),
                context.Session.Id,
                context.Node.Id,
                new ContactMessage(
                    context.ContactKey[..6],
                    1,
                    MessageTextType.Plain,
                    context.Now,
                    "incoming",
                    Array.Empty<byte>(),
                    null),
                context.Now,
                null,
                null),
            CancellationToken);

        await Task.WhenAll(save, ingest);

        Assert.Equal(
            Assert.IsType<DraftRecord>(await save).ConversationId,
            (await ingest).ConversationId);
        Assert.Single(await context.Storage.History.GetConversationsAsync(
            context.Node.Id, 10, CancellationToken));
    }

    [Fact]
    public async Task TrackerDoesNotLetLateSaveOverwriteNewerRevision()
    {
        var store = new FakeDraftStore();
        var tracker = new DraftWriteTracker(store, TimeProvider.System);
        var target = Target(1);
        await tracker.LoadTextAsync(target, CancellationToken);
        tracker.Update(target, "one", 1);
        var first = tracker.FlushAsync(target, CancellationToken);
        await store.FirstSaveStarted.Task.WaitAsync(CancellationToken);
        tracker.Update(target, "two", 2);
        store.FirstSaveGate.SetResult();

        await first;

        Assert.Equal(["one", "two"], store.SavedTexts);
        Assert.Equal("two", await tracker.LoadTextAsync(target, CancellationToken));
    }

    [Fact]
    public async Task TrackerRetainsFailedRevisionAndRetriesNewestText()
    {
        var store = new FakeDraftStore();
        store.Failures.Enqueue(new IOException("disk full"));
        var tracker = new DraftWriteTracker(store, TimeProvider.System);
        var durable = (IDurableDraftWrites)tracker;
        var target = Target(2);
        tracker.Update(target, "before", 1);

        await Assert.ThrowsAsync<IOException>(() => durable.FlushAsync(CancellationToken));
        Assert.True(durable.IsPaused);
        tracker.Update(target, "after", 2);

        await durable.RetryAsync(CancellationToken);

        Assert.False(durable.IsPaused);
        Assert.Equal("after", store.SavedTexts[^1]);
        Assert.Equal("after", await tracker.LoadTextAsync(target, CancellationToken));
    }

    private static DraftTarget Target(byte seed) => new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        null,
        ConversationKind.Contact,
        Enumerable.Repeat(seed, 32).ToArray());

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class FakeDraftStore : IDraftStore
    {
        private DraftRecord? _record;
        private int _saveCount;

        public Queue<Exception> Failures { get; } = [];
        public List<string> SavedTexts { get; } = [];
        public TaskCompletionSource FirstSaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstSaveGate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DraftRecord?> GetAsync(
            DraftTarget target,
            CancellationToken cancellationToken = default) => Task.FromResult(_record);

        public async Task<DraftRecord?> SaveAsync(
            DraftTarget target,
            string text,
            DateTimeOffset updatedUtc,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _saveCount);
            if (call == 1 && Failures.Count == 0)
            {
                FirstSaveStarted.TrySetResult();
                await FirstSaveGate.Task.WaitAsync(cancellationToken);
            }

            if (Failures.TryDequeue(out var failure))
            {
                throw failure;
            }

            SavedTexts.Add(text);
            _record = text.Length == 0
                ? null
                : new DraftRecord(target.ConversationId ?? Guid.NewGuid(), text, updatedUtc);
            return _record;
        }
    }

    private sealed class DraftContext : IAsyncDisposable
    {
        private readonly string _directory;

        private DraftContext(
            string directory,
            LocalStorage storage,
            NodeRecord node,
            SessionRecord session,
            DateTimeOffset now,
            byte[] contactKey,
            byte[] serviceKey,
            byte[] channelFingerprint)
        {
            _directory = directory;
            Storage = storage;
            Node = node;
            Session = session;
            Now = now;
            ContactKey = contactKey;
            ServiceKey = serviceKey;
            ChannelFingerprint = channelFingerprint;
        }

        public LocalStorage Storage { get; private set; }
        public NodeRecord Node { get; }
        public SessionRecord Session { get; }
        public DateTimeOffset Now { get; }
        public byte[] ContactKey { get; }
        public byte[] ServiceKey { get; }
        public byte[] ChannelFingerprint { get; }

        public DraftTarget ContactTarget() =>
            new(Node.Id, null, ConversationKind.Contact, ContactKey);

        public DraftTarget ChannelTarget() =>
            new(Node.Id, null, ConversationKind.Channel, ChannelFingerprint);

        public async Task ReopenAsync()
        {
            await Storage.DisposeAsync();
            Storage = await LocalStorage.OpenAsync(new TestPaths(_directory), CancellationToken);
        }

        public static async Task<DraftContext> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "MeshCoreMessenger.Drafts.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var storage = await LocalStorage.OpenAsync(new TestPaths(directory), CancellationToken);
            var now = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
            var profile = new ConnectionProfile
            {
                Id = Guid.NewGuid(),
                Name = "Draft test",
                Transport = ConnectionTransportKind.Tcp,
                TcpHost = "127.0.0.1",
                TcpPort = 5000,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            await storage.ConnectionProfiles.SaveAsync(profile, CancellationToken);
            var node = await storage.Nodes.FindOrCreateAsync(Key(200), "Node", now, CancellationToken);
            var session = new SessionRecord(Guid.NewGuid(), profile.Id, node.Id, now, null, null);
            await storage.Sessions.CreateAsync(session, CancellationToken);
            var contactKey = Key(10);
            var serviceKey = Key(50);
            var fingerprint = Key(90);
            await storage.Directories.ApplySnapshotAsync(
                node.Id,
                session.Id,
                [
                    Contact(contactKey, "Chat", (int)AdvertisementType.Chat, now),
                    Contact(serviceKey, "Repeater", (int)AdvertisementType.Repeater, now),
                ],
                [new DirectoryChannelSnapshot(1, "Channel", fingerprint, ChannelAccessKind.PublicOrHashtag)],
                now,
                CancellationToken);
            return new DraftContext(
                directory, storage, node, session, now, contactKey, serviceKey, fingerprint);
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

        private static DirectoryContactSnapshot Contact(
            byte[] key,
            string name,
            int type,
            DateTimeOffset now) =>
            new(key, name, type, 0, Enumerable.Repeat(key[0], 64).ToArray(), now, 0, 0);

        private static byte[] Key(byte seed) =>
            Enumerable.Range(0, 32).Select(index => (byte)(seed + index)).ToArray();
    }

    private sealed record TestPaths(string DataDirectory) : IAppPaths
    {
        public string DatabasePath => Path.Combine(DataDirectory, "messenger.db");
        public string BackupsDirectory => Path.Combine(DataDirectory, "backups");
    }
}
