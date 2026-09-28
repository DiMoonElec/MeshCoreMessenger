using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class MessengerEndToEndTests
{
    [Fact]
    public async Task StartupReconnectAndShutdownCommitEveryAcceptedMessageToItsSession()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestPaths(temporary.Path);
        await using var storage = await LocalStorage.OpenAsync(paths, CancellationToken);
        var profiles = new ConnectionProfileManager(
            storage.ConnectionProfiles,
            storage.Settings,
            TimeProvider.System);
        var profile = await profiles.SaveAndSelectAsync(new ConnectionProfileDraft
        {
            Name = "End-to-end fake",
            Transport = ConnectionTransportKind.Tcp,
            TcpHost = "127.0.0.1",
            TcpPort = 5000,
            AutoConnect = true,
            Reconnect = true,
        }, CancellationToken);
        var contactKey = Key(40);
        var firstClient = new ScriptedClient("first", contactKey);
        var secondClient = new ScriptedClient("second", contactKey);
        var clients = new ScriptedClientFactory(firstClient, secondClient);
        var sessionCompletions = new SessionCompletionTracker(storage.Sessions);
        var sessions = new CompanionSessionFactory(
            clients,
            profiles,
            storage.Nodes,
            storage.Sessions,
            sessionCompletions,
            TimeProvider.System);
        await using var ingestor = new MessageIngestor(storage.IncomingMessages, TimeProvider.System);
        var attempts = new ConnectionAttemptFactory(
            sessions,
            new DirectoryService(storage.Directories, TimeProvider.System),
            storage.Directories,
            ingestor);
        var delay = new ControlledReconnectDelay();
        await using var supervisor = new ConnectionSupervisor(
            profiles,
            attempts,
            new ConnectionFailureClassifier(),
            delay,
            new ZeroJitter(),
            TimeProvider.System);

        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online, generation: 1);
        var firstSessionId = Assert.IsType<Guid>(supervisor.Snapshot.SessionId);

        firstClient.FailConnection();
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.RetryWaiting, generation: 1);
        Assert.Single(delay.Requests);
        delay.Complete(0);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online, generation: 2);
        var secondSessionId = Assert.IsType<Guid>(supervisor.Snapshot.SessionId);
        Assert.NotEqual(firstSessionId, secondSessionId);

        await supervisor.ShutdownAsync(CancellationToken);
        await ingestor.FlushAsync(CancellationToken);
        await sessionCompletions.FlushAsync(CancellationToken);

        Assert.Equal(2, clients.CreateCount);
        Assert.Equal(1, clients.MaxActiveCount);
        Assert.All(clients.Clients, client => Assert.True(client.IsDisposed));
        Assert.Equal(ExpectedCalls, firstClient.Calls);
        Assert.Equal(ExpectedCalls, secondClient.Calls);

        var rows = ReadStoredMessages(paths.DatabasePath);
        Assert.Equal(
            [
                "first-start", "first-directory", "first-drain", "first-close",
                "second-start", "second-directory", "second-drain", "second-close",
            ],
            rows.Select(row => row.Text));
        Assert.All(rows.Take(4), row => Assert.Equal(firstSessionId, row.SessionId));
        Assert.All(rows.Skip(4), row => Assert.Equal(secondSessionId, row.SessionId));

        var firstSession = await storage.Sessions.GetAsync(firstSessionId, CancellationToken);
        var secondSession = await storage.Sessions.GetAsync(secondSessionId, CancellationToken);
        Assert.NotNull(firstSession?.EndedUtc);
        Assert.NotNull(secondSession?.EndedUtc);
        Assert.Equal("Connection attempt ended", firstSession?.EndReason);
        Assert.Equal("Application shutdown", secondSession?.EndReason);

        var conversations = await storage.History.GetConversationsAsync(10, CancellationToken);
        var conversation = Assert.Single(conversations);
        Assert.Equal(profile.Id, (await profiles.GetSelectedProfileAsync(CancellationToken))?.Id);
        Assert.Equal(8, (await storage.History.GetMessagesAsync(
            conversation.Id,
            null,
            20,
            CancellationToken)).Count);
    }

    private static readonly string[] ExpectedCalls =
    [
        "Connect",
        "Start",
        "GetContacts",
        "GetChannels",
        "DrainMessages",
        "FlushEvents",
        "Disconnect",
        "FlushEvents",
        "Dispose",
    ];

    private static IReadOnlyList<StoredRow> ReadStoredMessages(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Text, SessionId FROM Messages ORDER BY LocalSequence;";
        using var reader = command.ExecuteReader();
        var rows = new List<StoredRow>();
        while (reader.Read())
        {
            rows.Add(new StoredRow(reader.GetString(0), Guid.Parse(reader.GetString(1))));
        }

        return rows;
    }

    private static async Task WaitForStateAsync(
        IConnectionSupervisor supervisor,
        ConnectionSupervisorState state,
        long generation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        while (supervisor.Snapshot.State != state || supervisor.Snapshot.Generation != generation)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static ContactMessage Message(byte[] contactKey, string text) =>
        new(
            contactKey[..6],
            1,
            MessageTextType.Plain,
            DateTimeOffset.UtcNow,
            text,
            Array.Empty<byte>(),
            null);

    private static Contact Contact(byte[] publicKey) =>
        new(
            publicKey,
            AdvertisementType.Chat,
            0,
            0xFF,
            new byte[64],
            "Remote contact",
            1,
            0,
            0,
            0);

    private static SelfInfo Self() =>
        new(
            AdvertisementType.Chat,
            1,
            10,
            Key(100),
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
            "Fake local node");

    private static byte[] Key(byte seed) =>
        Enumerable.Range(0, 32).Select(index => (byte)(seed + index)).ToArray();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed record StoredRow(string Text, Guid SessionId);

    private sealed class ScriptedClientFactory(params ScriptedClient[] clients) : IMeshCoreClientFactory
    {
        private readonly Queue<ScriptedClient> _remaining = new(clients);
        private int _activeCount;

        public IReadOnlyList<ScriptedClient> Clients { get; } = clients;
        public int CreateCount { get; private set; }
        public int MaxActiveCount { get; private set; }

        public ICompanionClient Create(ConnectionProfile profile)
        {
            Assert.NotEmpty(_remaining);
            Assert.Equal(0, Volatile.Read(ref _activeCount));
            var client = _remaining.Dequeue();
            CreateCount++;
            var active = Interlocked.Increment(ref _activeCount);
            MaxActiveCount = Math.Max(MaxActiveCount, active);
            client.Disposed += (_, _) => Interlocked.Decrement(ref _activeCount);
            return client;
        }
    }

    private sealed class ScriptedClient(string name, byte[] contactKey) : ICompanionClient
    {
        private EventHandler<MeshCoreConnectionStateChangedEventArgs>? _connectionStateChanged;
        private EventHandler<MessageReceivedEventArgs>? _messageReceived;
        private bool _drained;
        private bool _closeMessageEmitted;

        public List<string> Calls { get; } = [];
        public MeshCoreConnectionState State { get; private set; } = MeshCoreConnectionState.Disconnected;
        public bool IsConnected => State == MeshCoreConnectionState.Connected;
        public bool IsStarted { get; private set; }
        public bool IsDisposed { get; private set; }

        public event EventHandler? Disposed;
        public event EventHandler<MeshCoreConnectionStateChangedEventArgs>? ConnectionStateChanged
        {
            add => _connectionStateChanged += value;
            remove => _connectionStateChanged -= value;
        }
        public event EventHandler<MeshCoreClientErrorEventArgs>? BackgroundError { add { } remove { } }
        public event EventHandler<CompanionPacketEventArgs>? PacketReceived { add { } remove { } }
        public event EventHandler<CompanionPacketEventArgs>? PushPacketReceived { add { } remove { } }
        public event EventHandler<CompanionPacketEventArgs>? UnhandledPacketReceived { add { } remove { } }
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived
        {
            add => _messageReceived += value;
            remove => _messageReceived -= value;
        }
        public event EventHandler<AdvertisementReceivedEventArgs>? AdvertisementReceived { add { } remove { } }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("Connect");
            SetConnectionState(MeshCoreConnectionState.Connected);
            return Task.CompletedTask;
        }

        public Task<SelfInfo> StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("Start");
            IsStarted = true;
            EmitMessage($"{name}-start");
            return Task.FromResult(Self());
        }

        public Task<IReadOnlyList<Contact>> GetContactsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("GetContacts");
            EmitMessage($"{name}-directory");
            return Task.FromResult<IReadOnlyList<Contact>>([Contact(contactKey)]);
        }

        public Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("GetChannels");
            return Task.FromResult<IReadOnlyList<ChannelInfo>>([]);
        }

        public Task DrainMessagesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("DrainMessages");
            if (!_drained)
            {
                _drained = true;
                EmitMessage($"{name}-drain");
            }
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("Disconnect");
            IsStarted = false;
            SetConnectionState(MeshCoreConnectionState.Disconnected);
            return Task.CompletedTask;
        }

        public Task FlushEventsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("FlushEvents");
            if (State == MeshCoreConnectionState.Disconnected && !_closeMessageEmitted)
            {
                _closeMessageEmitted = true;
                EmitMessage($"{name}-close");
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Calls.Add("Dispose");
            IsDisposed = true;
            Disposed?.Invoke(this, EventArgs.Empty);
            return ValueTask.CompletedTask;
        }

        public void FailConnection() => SetConnectionState(MeshCoreConnectionState.Faulted);

        private void EmitMessage(string text) =>
            _messageReceived?.Invoke(this, new MessageReceivedEventArgs(Message(contactKey, text)));

        private void SetConnectionState(MeshCoreConnectionState state)
        {
            var previous = State;
            State = state;
            _connectionStateChanged?.Invoke(
                this,
                new MeshCoreConnectionStateChangedEventArgs(previous, state));
        }
    }

    private sealed class ControlledReconnectDelay : IReconnectDelay
    {
        public List<TaskCompletionSource> Requests { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(completion);
            return completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete(int index) => Requests[index].TrySetResult();
    }

    private sealed class ZeroJitter : IReconnectJitter
    {
        public double GetJitterFraction(int retryNumber) => 0;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "MeshCoreMessenger.EndToEnd.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private sealed record TestPaths(string DataDirectory) : IAppPaths
    {
        public string DatabasePath { get; } = System.IO.Path.Combine(DataDirectory, "messenger.db");
        public string BackupsDirectory { get; } = System.IO.Path.Combine(DataDirectory, "backups");
    }
}
