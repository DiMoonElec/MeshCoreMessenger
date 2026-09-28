using System.Collections.Concurrent;
using System.Reflection;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Packets;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class ReceiveCoordinatorTests
{
    [Fact]
    public async Task InitialSynchronizationIncludesMessageBufferedDuringStart()
    {
        await using var context = await CoordinatorContext.CreateAsync();
        var contactKey = Key(10);
        var client = context.Client;
        client.Contacts = [Contact(contactKey, "Alice")];
        client.MessageDuringStart = Message(contactKey[..6], "during start");
        await using var session = await context.CreateStartedSessionAsync();
        await using var coordinator = context.CreateCoordinator();

        await coordinator.SynchronizeAsync(session, CancellationToken);

        var received = Assert.Single(context.Store.Stored);
        Assert.Equal("during start", Assert.IsType<ContactMessage>(received.Message).Text);
        Assert.Equal(session.SessionId, received.SessionId);
        Assert.Equal(context.NodeId, received.NodeId);
        Assert.Equal(1, client.DrainCount);
        Assert.Equal(ReceiveCoordinatorState.Online, coordinator.State);
    }

    [Fact]
    public async Task MessagesWaitingDuringDrainCoalescesIntoOneAdditionalPass()
    {
        await using var context = await CoordinatorContext.CreateAsync();
        var client = context.Client;
        client.DrainAction = () =>
        {
            if (client.DrainCount == 1)
            {
                client.EmitMessagesWaiting();
                client.EmitMessage(new ChannelMessage(2, 1, MessageTextType.Plain, DateTimeOffset.UtcNow, "first", null));
            }
            else if (client.DrainCount == 2)
            {
                client.EmitMessage(new ChannelMessage(2, 1, MessageTextType.Plain, DateTimeOffset.UtcNow, "second", null));
            }
            return Task.CompletedTask;
        };
        await using var session = await context.CreateStartedSessionAsync();
        await using var coordinator = context.CreateCoordinator();

        await coordinator.SynchronizeAsync(session, CancellationToken);

        Assert.Equal(2, client.DrainCount);
        Assert.Equal(["first", "second"], context.Store.Stored.Select(item => Assert.IsType<ChannelMessage>(item.Message).Text));
    }

    [Fact]
    public async Task CallbackCompletedByLibraryFlushIsCommittedBeforeInitialSynchronizationCompletes()
    {
        await using var context = await CoordinatorContext.CreateAsync();
        context.Client.FlushAction = () =>
        {
            if (context.Client.DrainCount == 1)
            {
                context.Client.EmitMessage(new ChannelMessage(1, 1, MessageTextType.Plain, DateTimeOffset.UtcNow, "flush callback", null));
                context.Client.FlushAction = null;
            }
            return Task.CompletedTask;
        };
        await using var session = await context.CreateStartedSessionAsync();
        await using var coordinator = context.CreateCoordinator();

        await coordinator.SynchronizeAsync(session, CancellationToken);

        Assert.Equal("flush callback", Assert.IsType<ChannelMessage>(Assert.Single(context.Store.Stored).Message).Text);
    }

    [Fact]
    public async Task DrainTimeoutMarksSessionForReplacementAndDoesNotRetryIt()
    {
        await using var context = await CoordinatorContext.CreateAsync();
        context.Client.DrainAction = () => throw new TimeoutException("SYNC_NEXT_MESSAGE timed out");
        await using var session = await context.CreateStartedSessionAsync();
        await using var coordinator = context.CreateCoordinator();

        var exception = await Assert.ThrowsAsync<ReceiveDrainTimeoutException>(
            () => coordinator.SynchronizeAsync(session, CancellationToken));
        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal(1, context.Client.DrainCount);
        Assert.Equal(ReceiveCoordinatorState.NeedsAttention, coordinator.State);
        await Assert.ThrowsAsync<ReceiveDrainTimeoutException>(
            () => coordinator.SynchronizeAsync(session, CancellationToken));
        Assert.Equal(1, context.Client.DrainCount);
    }

    [Fact]
    public async Task StorageFailurePausesDrainUntilExplicitRetry()
    {
        await using var context = await CoordinatorContext.CreateAsync();
        context.Store.FailuresRemaining = 1;
        context.Client.DrainAction = () =>
        {
            context.Client.EmitMessage(new ChannelMessage(1, 1, MessageTextType.Plain, DateTimeOffset.UtcNow, "durable", null));
            return Task.CompletedTask;
        };
        await using var session = await context.CreateStartedSessionAsync();
        await using var coordinator = context.CreateCoordinator();

        await Assert.ThrowsAnyAsync<Exception>(() => coordinator.SynchronizeAsync(session, CancellationToken));
        Assert.Equal(ReceiveCoordinatorState.NeedsAttention, coordinator.State);
        var drainsBeforeRetry = context.Client.DrainCount;

        await coordinator.RetryAsync(CancellationToken);
        await WaitUntilAsync(() => coordinator.State == ReceiveCoordinatorState.Online, CancellationToken);
        Assert.True(context.Client.DrainCount > drainsBeforeRetry);
        Assert.NotEmpty(context.Store.Stored);
    }

    [Fact]
    public async Task ChangedChannelSlotKeepsBacklogUnknownUntilNewBindingIsActivated()
    {
        await using var context = await CoordinatorContext.CreateAsync();
        context.Client.Channels = [new ChannelInfo(4, "New", Enumerable.Repeat((byte)2, 16).ToArray())];
        await using var session = await context.CreateStartedSessionAsync();
        await context.SeedChannelAsync(session, 4, "Old", 1);
        context.Client.DrainAction = () =>
        {
            context.Client.EmitMessage(new ChannelMessage(4, 1, MessageTextType.Plain, DateTimeOffset.UtcNow, $"pass {context.Client.DrainCount}", null));
            if (context.Client.DrainCount == 1) context.Client.EmitMessagesWaiting();
            return Task.CompletedTask;
        };
        await using var coordinator = context.CreateCoordinator();

        await coordinator.SynchronizeAsync(session, CancellationToken);

        var messages = context.Store.Stored.ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Null(messages[0].StableChannelBinding);
        Assert.NotNull(messages[0].UnknownChannelIdentity);
        Assert.NotNull(messages[1].StableChannelBinding);
        Assert.Null(messages[1].UnknownChannelIdentity);
    }

    [Fact]
    public async Task QuiesceKeepsEventConsumerAliveThroughSessionBarrierAndIngestCommit()
    {
        await using var context = await CoordinatorContext.CreateAsync();
        var contactKey = Key(44);
        context.Client.Contacts = [Contact(contactKey, "Closing sender")];
        await using var session = await context.CreateStartedSessionAsync();
        await using var coordinator = context.CreateCoordinator();
        await coordinator.SynchronizeAsync(session, CancellationToken);
        context.Client.FlushAction = () =>
        {
            context.Client.EmitMessage(Message(contactKey[..6], "during close"));
            context.Client.FlushAction = null;
            return Task.CompletedTask;
        };

        await coordinator.QuiesceAsync(CancellationToken);
        await session.StopAsync("Reconnect", CancellationToken);
        await coordinator.CompleteAfterSessionStopAsync(CancellationToken);

        var stored = Assert.Single(context.Store.Stored);
        Assert.Equal("during close", Assert.IsType<ContactMessage>(stored.Message).Text);
        Assert.Equal(ReceiveCoordinatorState.Stopped, coordinator.State);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    private static Contact Contact(byte[] key, string name) => new(key, AdvertisementType.Chat, 0, 0xFF, new byte[64], name, 1, 0, 0, 0);
    private static ContactMessage Message(byte[] prefix, string text) => new(prefix, 1, MessageTextType.Plain, DateTimeOffset.UtcNow, text, Array.Empty<byte>(), null);
    private static SelfInfo Self(byte[] key) => new(AdvertisementType.Chat, 1, 10, key, 0, 0, 0, 0, 0, false, 869.525, 250, 10, 5, "Node");
    private static byte[] Key(byte seed) => Enumerable.Range(0, 32).Select(i => (byte)(seed + i)).ToArray();
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class CaptureStore : IIncomingMessageStore
    {
        private int _failuresRemaining;
        public int FailuresRemaining { set => _failuresRemaining = value; }
        public ConcurrentQueue<IncomingMessageEnvelope> Stored { get; } = [];
        public Task<StoredIncomingMessage> StoreAsync(IncomingMessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Decrement(ref _failuresRemaining) >= 0)
            {
                throw new IOException("Disk is temporarily unavailable.");
            }
            Stored.Enqueue(envelope);
            return Task.FromResult(new StoredIncomingMessage(Guid.NewGuid(), envelope.EventId, Guid.NewGuid(), Stored.Count, true));
        }
    }

    private sealed class FakeClient : ICompanionClient
    {
        private EventHandler<CompanionPacketEventArgs>? _push;
        private EventHandler<MessageReceivedEventArgs>? _messages;
        public IReadOnlyList<Contact> Contacts { get; set; } = [];
        public IReadOnlyList<ChannelInfo> Channels { get; set; } = [];
        public ReceivedMessage? MessageDuringStart { get; set; }
        public Func<Task>? DrainAction { get; set; }
        public Func<Task>? FlushAction { get; set; }
        public int DrainCount { get; private set; }
        public MeshCoreConnectionState State { get; private set; } = MeshCoreConnectionState.Disconnected;
        public bool IsConnected => State == MeshCoreConnectionState.Connected;
        public bool IsStarted { get; private set; }
        public event EventHandler<MeshCoreConnectionStateChangedEventArgs>? ConnectionStateChanged { add { } remove { } }
        public event EventHandler<MeshCoreClientErrorEventArgs>? BackgroundError { add { } remove { } }
        public event EventHandler<CompanionPacketEventArgs>? PacketReceived { add { } remove { } }
        public event EventHandler<CompanionPacketEventArgs>? PushPacketReceived { add => _push += value; remove => _push -= value; }
        public event EventHandler<CompanionPacketEventArgs>? UnhandledPacketReceived { add { } remove { } }
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived { add => _messages += value; remove => _messages -= value; }
        public event EventHandler<AdvertisementReceivedEventArgs>? AdvertisementReceived { add { } remove { } }
        public Task ConnectAsync(CancellationToken cancellationToken = default) { State = MeshCoreConnectionState.Connected; return Task.CompletedTask; }
        public Task<SelfInfo> StartAsync(CancellationToken cancellationToken = default)
        {
            IsStarted = true;
            if (MessageDuringStart is not null) EmitMessage(MessageDuringStart);
            return Task.FromResult(Self(Key(200)));
        }
        public Task<IReadOnlyList<Contact>> GetContactsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Contacts);
        public Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Channels);
        public async Task DrainMessagesAsync(CancellationToken cancellationToken = default) { DrainCount++; if (DrainAction is not null) await DrainAction(); }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) { State = MeshCoreConnectionState.Disconnected; return Task.CompletedTask; }
        public Task FlushEventsAsync(CancellationToken cancellationToken = default) => FlushAction?.Invoke() ?? Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void EmitMessage(ReceivedMessage message) => _messages?.Invoke(this, new MessageReceivedEventArgs(message));
        public void EmitMessagesWaiting()
        {
            var constructor = typeof(RawCompanionPacket).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                [typeof(byte), typeof(ReadOnlyMemory<byte>)], null)!;
            var packet = (CompanionPacket)constructor.Invoke([(byte)PacketType.MessagesWaiting, new ReadOnlyMemory<byte>([(byte)PacketType.MessagesWaiting])]);
            _push?.Invoke(this, new CompanionPacketEventArgs(packet));
        }
    }

    private sealed class CoordinatorContext : IAsyncDisposable
    {
        private readonly string _root;
        private readonly LocalStorage _storage;
        private readonly ConnectionProfile _profile;
        private readonly CompanionSessionFactory _sessions;
        public CoordinatorContext(string root, LocalStorage storage, ConnectionProfile profile, FakeClient client, CompanionSessionFactory sessions, CaptureStore store)
        { _root = root; _storage = storage; _profile = profile; Client = client; _sessions = sessions; Store = store; }
        public FakeClient Client { get; }
        public CaptureStore Store { get; }
        public Guid NodeId { get; private set; }
        public static async Task<CoordinatorContext> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "MeshCoreMessenger.Receive.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var storage = await LocalStorage.OpenAsync(new Paths(root), CancellationToken);
            var now = DateTimeOffset.UtcNow;
            var profile = new ConnectionProfile { Id = Guid.NewGuid(), Name = "Test", Transport = ConnectionTransportKind.Serial, SerialPortName = "/dev/cu.fake", BaudRate = 115200, OpenDelayMilliseconds = 0, CommandTimeoutMilliseconds = 1000, AcknowledgementTimeoutMilliseconds = 2000, CreatedUtc = now, UpdatedUtc = now };
            await storage.ConnectionProfiles.SaveAsync(profile, CancellationToken);
            var client = new FakeClient(); var store = new CaptureStore();
            var profiles = new ConnectionProfileManager(storage.ConnectionProfiles, storage.Settings, TimeProvider.System);
            return new CoordinatorContext(root, storage, profile, client, new CompanionSessionFactory(new OneClientFactory(client), profiles, storage.Nodes, storage.Sessions, new SessionCompletionTracker(storage.Sessions), TimeProvider.System), store);
        }
        public async Task<CompanionSession> CreateStartedSessionAsync()
        {
            var session = await _sessions.CreateAsync(_profile, 1, CancellationToken);
            var started = await session.StartAsync(CancellationToken);
            NodeId = started.NodeId;
            return session;
        }
        public ReceiveCoordinator CreateCoordinator() => new(new DirectoryService(_storage.Directories, TimeProvider.System), _storage.Directories, new MessageIngestor(Store, TimeProvider.System));
        public async Task SeedChannelAsync(CompanionSession session, byte slot, string name, byte secret)
        {
            await _storage.Directories.ApplySnapshotAsync(
                NodeId, session.SessionId, [],
                [new DirectoryChannelSnapshot(slot, name, System.Security.Cryptography.SHA256.HashData(Enumerable.Repeat(secret, 16).ToArray()), ChannelAccessKind.Unknown)],
                DateTimeOffset.UtcNow, CancellationToken);
        }
        public async ValueTask DisposeAsync() { await _storage.DisposeAsync(); try { Directory.Delete(_root, true); } catch (DirectoryNotFoundException) { } }
        private sealed class OneClientFactory(FakeClient client) : IMeshCoreClientFactory { public ICompanionClient Create(ConnectionProfile profile) => client; }
        private sealed record Paths(string DataDirectory) : IAppPaths { public string DatabasePath { get; } = Path.Combine(DataDirectory, "messenger.db"); public string BackupsDirectory { get; } = Path.Combine(DataDirectory, "backups"); }
    }
}
