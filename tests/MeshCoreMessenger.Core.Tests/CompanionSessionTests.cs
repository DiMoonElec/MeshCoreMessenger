using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class CompanionSessionTests
{
    [Fact]
    public async Task SubscribesBeforeConnectAndPersistsSessionBeforeFirstCallback()
    {
        await using var context = await TestContext.CreateAsync();
        var profile = await context.SaveProfileAsync("First");
        var client = new FakeCompanionClient(CreateSelfInfo(Key(1), "Node one"));
        context.Clients.Enqueue(client);
        var session = await context.Factory.CreateAsync(profile, 7, CancellationToken);
        client.ConnectAction = async cancellationToken =>
        {
            Assert.True(client.ConnectionSubscriberCount > 0);
            Assert.True(client.ErrorSubscriberCount > 0);
            Assert.Equal(5, client.DataSubscriberCount);
            var persisted = await context.Storage.Sessions.GetAsync(session.SessionId, cancellationToken);
            Assert.NotNull(persisted);
            Assert.Null(persisted.NodeId);
            client.EmitConnectionState(MeshCoreConnectionState.Disconnected, MeshCoreConnectionState.Connecting);
        };
        client.StartAction = cancellationToken =>
        {
            Assert.True(client.ConnectionSubscriberCount > 0);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(client.SelfInfo);
        };

        var result = await session.StartAsync(CancellationToken);

        Assert.Equal(session.SessionId, result.SessionId);
        Assert.Equal(7, result.Generation);
        Assert.False(result.RequiresNodeConfirmation);
        Assert.Equal(CompanionSessionState.Identified, session.State);
        var stored = await context.Storage.Sessions.GetAsync(session.SessionId, CancellationToken);
        Assert.Equal(result.NodeId, stored?.NodeId);
        var updatedProfile = await context.Storage.ConnectionProfiles.GetAsync(profile.Id, CancellationToken);
        Assert.Equal(Key(1), updatedProfile?.ExpectedNodePublicKey);

        await session.StopAsync("Test completed", CancellationToken);
        Assert.Equal(["Connect", "Start", "Disconnect", "Flush", "Dispose"], client.Calls);
        Assert.Equal(0, client.ConnectionSubscriberCount);
        Assert.Equal(0, client.ErrorSubscriberCount);
        Assert.Equal(0, client.DataSubscriberCount);
        stored = await context.Storage.Sessions.GetAsync(session.SessionId, CancellationToken);
        Assert.NotNull(stored?.EndedUtc);
        Assert.Equal("Test completed", stored?.EndReason);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task SameFullKeyThroughDifferentProfilesUsesOneNodeId()
    {
        await using var context = await TestContext.CreateAsync();
        var firstProfile = await context.SaveProfileAsync("Serial");
        var secondProfile = await context.SaveProfileAsync("TCP", tcp: true);
        var key = Key(20);
        context.Clients.Enqueue(new FakeCompanionClient(CreateSelfInfo(key, "Shared node")));
        context.Clients.Enqueue(new FakeCompanionClient(CreateSelfInfo(key, "Renamed node")));

        await using var first = await context.Factory.CreateAsync(firstProfile, 1, CancellationToken);
        var firstResult = await first.StartAsync(CancellationToken);
        await first.StopAsync("Switch transport", CancellationToken);
        await using var second = await context.Factory.CreateAsync(secondProfile, 2, CancellationToken);
        var secondResult = await second.StartAsync(CancellationToken);

        Assert.Equal(firstResult.NodeId, secondResult.NodeId);
        var node = await context.Storage.Nodes.GetAsync(firstResult.NodeId, CancellationToken);
        Assert.Equal("Renamed node", node?.LastName);
        await second.StopAsync("Done", CancellationToken);
    }

    [Fact]
    public async Task DifferentKeyOnSameProfileRequiresExplicitConfirmationAndKeepsHistoriesSeparate()
    {
        await using var context = await TestContext.CreateAsync();
        var profile = await context.SaveProfileAsync("One address");
        var expectedKey = Key(40);
        var unexpectedKey = Key(80);
        context.Clients.Enqueue(new FakeCompanionClient(CreateSelfInfo(expectedKey, "Expected")));
        var unexpectedClient = new FakeCompanionClient(CreateSelfInfo(unexpectedKey, "Unexpected"));
        context.Clients.Enqueue(unexpectedClient);

        await using var first = await context.Factory.CreateAsync(profile, 1, CancellationToken);
        var expected = await first.StartAsync(CancellationToken);
        await first.StopAsync("Reconnect", CancellationToken);
        var boundProfile = await context.Storage.ConnectionProfiles.GetAsync(profile.Id, CancellationToken);

        await using var second = await context.Factory.CreateAsync(boundProfile!, 2, CancellationToken);
        var unexpected = await second.StartAsync(CancellationToken);

        Assert.NotEqual(expected.NodeId, unexpected.NodeId);
        Assert.True(unexpected.RequiresNodeConfirmation);
        Assert.Equal(CompanionSessionState.NeedsAttention, second.State);
        Assert.Equal(["Connect", "Start"], unexpectedClient.Calls);
        var unchanged = await context.Storage.ConnectionProfiles.GetAsync(profile.Id, CancellationToken);
        Assert.Equal(expectedKey, unchanged?.ExpectedNodePublicKey);

        await second.UseConnectedNodeAsync(CancellationToken);

        Assert.Equal(CompanionSessionState.Identified, second.State);
        var rebound = await context.Storage.ConnectionProfiles.GetAsync(profile.Id, CancellationToken);
        Assert.Equal(unexpectedKey, rebound?.ExpectedNodePublicKey);
        await second.StopAsync("Done", CancellationToken);
    }

    [Fact]
    public async Task CanceledStartEndsPersistedSession()
    {
        await using var context = await TestContext.CreateAsync();
        var profile = await context.SaveProfileAsync("Canceled");
        var client = new FakeCompanionClient(CreateSelfInfo(Key(1), "Node"))
        {
            ConnectAction = cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
        };
        context.Clients.Enqueue(client);
        await using var session = await context.Factory.CreateAsync(profile, 1, CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.StartAsync(cancellation.Token));

        var stored = await context.Storage.Sessions.GetAsync(session.SessionId, CancellationToken);
        Assert.NotNull(stored?.EndedUtc);
        Assert.Equal("Start canceled", stored?.EndReason);
        Assert.Equal(CompanionSessionState.Stopped, session.State);
        Assert.Equal(["Disconnect", "Flush", "Dispose"], client.Calls);
    }

    [Fact]
    public async Task StartFailureEndsPersistedSession()
    {
        await using var context = await TestContext.CreateAsync();
        var profile = await context.SaveProfileAsync("Failure");
        var client = new FakeCompanionClient(CreateSelfInfo(Key(1), "Node"))
        {
            StartAction = _ => throw new IOException("APP_START failed"),
        };
        context.Clients.Enqueue(client);
        await using var session = await context.Factory.CreateAsync(profile, 1, CancellationToken);

        var exception = await Assert.ThrowsAsync<IOException>(() => session.StartAsync(CancellationToken));

        Assert.Equal("APP_START failed", exception.Message);
        var stored = await context.Storage.Sessions.GetAsync(session.SessionId, CancellationToken);
        Assert.NotNull(stored?.EndedUtc);
        Assert.Equal("Start failed", stored?.EndReason);
        Assert.Equal(CompanionSessionState.Failed, session.State);
    }

    [Fact]
    public async Task LateLifecycleCallbackRetainsOwningSessionAndGeneration()
    {
        await using var context = await TestContext.CreateAsync();
        var profile = await context.SaveProfileAsync("Events");
        var client = new FakeCompanionClient(CreateSelfInfo(Key(1), "Node"));
        context.Clients.Enqueue(client);
        await using var session = await context.Factory.CreateAsync(profile, 42, CancellationToken);
        await session.StartAsync(CancellationToken);
        client.DisconnectAction = cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            client.EmitConnectionState(MeshCoreConnectionState.Connected, MeshCoreConnectionState.Disconnected);
            return Task.CompletedTask;
        };

        await session.StopAsync("Late event", CancellationToken);

        var events = new List<CompanionSessionEvent>();
        await foreach (var item in session.Events.ReadAllAsync(CancellationToken))
        {
            events.Add(item);
        }
        Assert.Contains(events, item =>
            item.SessionId == session.SessionId &&
            item.Generation == 42 &&
            item.CurrentConnectionState == MeshCoreConnectionState.Disconnected);
    }

    [Fact]
    public async Task DisconnectFailureStillFlushesDisposesAndEndsSession()
    {
        await using var context = await TestContext.CreateAsync();
        var profile = await context.SaveProfileAsync("Close failure");
        var client = new FakeCompanionClient(CreateSelfInfo(Key(1), "Node"))
        {
            DisconnectAction = _ => throw new IOException("Disconnect failed"),
        };
        context.Clients.Enqueue(client);
        await using var session = await context.Factory.CreateAsync(profile, 1, CancellationToken);
        await session.StartAsync(CancellationToken);

        var exception = await Assert.ThrowsAsync<IOException>(
            () => session.StopAsync("Close requested", CancellationToken));

        Assert.Equal("Disconnect failed", exception.Message);
        Assert.Equal(["Connect", "Start", "Disconnect", "Flush", "Dispose"], client.Calls);
        var stored = await context.Storage.Sessions.GetAsync(session.SessionId, CancellationToken);
        Assert.NotNull(stored?.EndedUtc);
        Assert.Equal("Close requested", stored?.EndReason);
    }

    [Fact]
    public async Task ReceivedMessageIsCopiedAndTaggedBeforeApplicationProcessing()
    {
        await using var context = await TestContext.CreateAsync();
        var profile = await context.SaveProfileAsync("Incoming event");
        var client = new FakeCompanionClient(CreateSelfInfo(Key(1), "Node"));
        context.Clients.Enqueue(client);
        await using var session = await context.Factory.CreateAsync(profile, 15, CancellationToken);
        await session.StartAsync(CancellationToken);
        byte[] prefix = [1, 2, 3, 4, 5, 6];
        client.EmitMessage(new ContactMessage(
            prefix,
            0xFF,
            MessageTextType.Plain,
            DateTimeOffset.UtcNow,
            "Test",
            ReadOnlyMemory<byte>.Empty,
            null));
        prefix[0] = 99;

        var copied = await session.Events.ReadAsync(CancellationToken);

        Assert.Equal(CompanionSessionEventKind.MessageReceived, copied.Kind);
        Assert.Equal(session.SessionId, copied.SessionId);
        Assert.Equal(15, copied.Generation);
        var message = Assert.IsType<ContactMessage>(copied.Message);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, message.ContactPublicKeyPrefix.ToArray());
        await session.StopAsync("Done", CancellationToken);
    }

    [Fact]
    public async Task ClientCreationFailureStillEndsSessionRecord()
    {
        await using var context = await TestContext.CreateAsync();
        var profile = await context.SaveProfileAsync("Factory failure");
        context.Clients.CreationError = new IOException("No client");

        await Assert.ThrowsAsync<IOException>(
            () => context.Factory.CreateAsync(profile, 1, CancellationToken));

        var sessions = await context.ReadAllSessionsAsync();
        var session = Assert.Single(sessions);
        Assert.NotNull(session.EndedUtc);
        Assert.Equal("Client creation failed", session.EndReason);
    }

    private static SelfInfo CreateSelfInfo(byte[] key, string name) => new(
        AdvertisementType.Chat,
        1,
        10,
        key,
        0,
        0,
        0,
        0,
        0,
        false,
        869.525,
        250,
        10,
        5,
        name);

    private static byte[] Key(byte seed) => Enumerable.Range(0, 32).Select(i => (byte)(seed + i)).ToArray();
    private static CancellationToken CancellationToken => Xunit.TestContext.Current.CancellationToken;

    private sealed class FakeClientFactory : IMeshCoreClientFactory
    {
        private readonly Queue<FakeCompanionClient> _clients = new();
        public Exception? CreationError { get; set; }

        public void Enqueue(FakeCompanionClient client) => _clients.Enqueue(client);

        public ICompanionClient Create(ConnectionProfile profile)
        {
            if (CreationError is not null)
            {
                throw CreationError;
            }

            return _clients.Dequeue();
        }
    }

    private sealed class FakeCompanionClient(SelfInfo selfInfo) : ICompanionClient
    {
        private EventHandler<MeshCoreConnectionStateChangedEventArgs>? _connectionStateChanged;
        private EventHandler<MeshCoreClientErrorEventArgs>? _backgroundError;
        private EventHandler<MessageReceivedEventArgs>? _messageReceived;
        private int _dataSubscriberCount;

        public SelfInfo SelfInfo { get; } = selfInfo;
        public List<string> Calls { get; } = [];
        public Func<CancellationToken, Task>? ConnectAction { get; set; }
        public Func<CancellationToken, Task<SelfInfo>>? StartAction { get; set; }
        public Func<CancellationToken, Task>? DisconnectAction { get; set; }
        public MeshCoreConnectionState State { get; private set; } = MeshCoreConnectionState.Disconnected;
        public bool IsConnected => State == MeshCoreConnectionState.Connected;
        public bool IsStarted { get; private set; }
        public int ConnectionSubscriberCount => _connectionStateChanged?.GetInvocationList().Length ?? 0;
        public int ErrorSubscriberCount => _backgroundError?.GetInvocationList().Length ?? 0;
        public int DataSubscriberCount => _dataSubscriberCount;

        public event EventHandler<MeshCoreConnectionStateChangedEventArgs>? ConnectionStateChanged
        {
            add => _connectionStateChanged += value;
            remove => _connectionStateChanged -= value;
        }

        public event EventHandler<MeshCoreClientErrorEventArgs>? BackgroundError
        {
            add => _backgroundError += value;
            remove => _backgroundError -= value;
        }

        public event EventHandler<CompanionPacketEventArgs>? PacketReceived
        {
            add => _dataSubscriberCount++;
            remove => _dataSubscriberCount--;
        }
        public event EventHandler<CompanionPacketEventArgs>? PushPacketReceived
        {
            add => _dataSubscriberCount++;
            remove => _dataSubscriberCount--;
        }
        public event EventHandler<CompanionPacketEventArgs>? UnhandledPacketReceived
        {
            add => _dataSubscriberCount++;
            remove => _dataSubscriberCount--;
        }
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived
        {
            add
            {
                _messageReceived += value;
                _dataSubscriberCount++;
            }
            remove
            {
                _messageReceived -= value;
                _dataSubscriberCount--;
            }
        }
        public event EventHandler<AdvertisementReceivedEventArgs>? AdvertisementReceived
        {
            add => _dataSubscriberCount++;
            remove => _dataSubscriberCount--;
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (ConnectAction is not null)
            {
                await ConnectAction(cancellationToken);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            Calls.Add("Connect");
            State = MeshCoreConnectionState.Connected;
        }

        public async Task<SelfInfo> StartAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("Start");
            var result = StartAction is null
                ? SelfInfo
                : await StartAction(cancellationToken);
            IsStarted = true;
            return result;
        }

        public Task<IReadOnlyList<Contact>> GetContactsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<Contact>>([]);
        }

        public Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ChannelInfo>>([]);
        }

        public Task DrainMessagesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("Drain");
            return Task.CompletedTask;
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("Disconnect");
            if (DisconnectAction is not null)
            {
                await DisconnectAction(cancellationToken);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            IsStarted = false;
            State = MeshCoreConnectionState.Disconnected;
        }

        public Task FlushEventsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("Flush");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Calls.Add("Dispose");
            return ValueTask.CompletedTask;
        }

        public void EmitConnectionState(
            MeshCoreConnectionState previous,
            MeshCoreConnectionState current) =>
            _connectionStateChanged?.Invoke(this, new MeshCoreConnectionStateChangedEventArgs(previous, current));

        public void EmitMessage(ReceivedMessage message) =>
            _messageReceived?.Invoke(this, new MessageReceivedEventArgs(message));
    }

    private sealed class TestContext : IAsyncDisposable
    {
        private readonly string _directory;

        private TestContext(string directory, LocalStorage storage, FakeClientFactory clients)
        {
            _directory = directory;
            Storage = storage;
            Clients = clients;
            var profileManager = new ConnectionProfileManager(
                storage.ConnectionProfiles,
                storage.Settings,
                TimeProvider.System);
            Factory = new CompanionSessionFactory(
                clients,
                profileManager,
                storage.Nodes,
                storage.Sessions,
                new SessionCompletionTracker(storage.Sessions),
                TimeProvider.System);
        }

        public LocalStorage Storage { get; }
        public FakeClientFactory Clients { get; }
        public CompanionSessionFactory Factory { get; }

        public static async Task<TestContext> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "MeshCoreMessenger.B2.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var storage = await LocalStorage.OpenAsync(new TestPaths(directory), CancellationToken);
            return new TestContext(directory, storage, new FakeClientFactory());
        }

        public async Task<ConnectionProfile> SaveProfileAsync(string name, bool tcp = false)
        {
            var now = DateTimeOffset.UtcNow;
            var profile = new ConnectionProfile
            {
                Id = Guid.NewGuid(),
                Name = name,
                Transport = tcp ? ConnectionTransportKind.Tcp : ConnectionTransportKind.Serial,
                TcpHost = tcp ? "127.0.0.1" : null,
                TcpPort = tcp ? 5000 : null,
                SerialPortName = tcp ? null : "/dev/cu.fake",
                BaudRate = tcp ? null : 115_200,
                OpenDelayMilliseconds = 0,
                CommandTimeoutMilliseconds = 1_000,
                AcknowledgementTimeoutMilliseconds = 2_000,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            await Storage.ConnectionProfiles.SaveAsync(profile, CancellationToken);
            return profile;
        }

        public async Task<IReadOnlyList<SessionRecord>> ReadAllSessionsAsync()
        {
            var ids = new List<Guid>();
            var database = Path.Combine(_directory, "messenger.db");
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Mode=ReadOnly");
            await connection.OpenAsync(CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id FROM Sessions ORDER BY StartedUtc;";
            await using var reader = await command.ExecuteReaderAsync(CancellationToken);
            while (await reader.ReadAsync(CancellationToken))
            {
                ids.Add(Guid.Parse(reader.GetString(0)));
            }

            var sessions = new List<SessionRecord>();
            foreach (var id in ids)
            {
                sessions.Add((await Storage.Sessions.GetAsync(id, CancellationToken))!);
            }
            return sessions;
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
    }

    private sealed record TestPaths(string DataDirectory) : IAppPaths
    {
        public string DatabasePath { get; } = Path.Combine(DataDirectory, "messenger.db");
        public string BackupsDirectory { get; } = Path.Combine(DataDirectory, "backups");
    }
}
