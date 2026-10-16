using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Tcp;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class BleIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FullGattPathSynchronizesDrainsAndPublishesOnlyCommittedMessages()
    {
        await using var f = await Fixture.Create();
        var commits = new ConcurrentQueue<Guid>();
        f.Ingest.MessageCommitted += (_, args) =>
        {
            // This is the UI notification boundary: a separate reader already sees the row.
            Assert.True(f.Scalar("SELECT COUNT(*) FROM Messages WHERE Id=$id", args.Message.MessageId) > 0);
            commits.Enqueue(args.Message.MessageId);
        };
        var device = await f.Connect();
        var node = f.Supervisor.Snapshot.NodeId!.Value;
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM Nodes"));
        Assert.Equal(2, (await f.Storage.Directories.GetCurrentContactsByPrefixAsync(node,
            new byte[] { 0xA1, 0xA1, 0xA1, 0xA1, 0xA1, 0xA1 }, Token)).Count +
            (await f.Storage.Directories.GetCurrentContactsByPrefixAsync(node,
            new byte[] { 0xB2, 0xB2, 0xB2, 0xB2, 0xB2, 0xB2 }, Token)).Count);
        Assert.NotNull(await f.Storage.Directories.GetActiveChannelBindingAsync(node, 0, Token));
        await f.Supervisor.ShutdownAsync(Token);
        Assert.Equal(3, commits.Count);
        Assert.Equal(3, commits.Distinct().Count());
        Assert.Equal(3, f.Scalar("SELECT COUNT(*) FROM Messages"));
        Assert.Contains(device.Commands, x => x[0] == (byte)CommandType.SyncNextMessage);
        Assert.DoesNotContain(device.Commands, x => x[0] is (byte)CommandType.SendTextMessage or (byte)CommandType.SendChannelTextMessage);
        Assert.Contains(device.Commands, x => x[0] == (byte)CommandType.GetDefaultFloodScope);
        Assert.DoesNotContain(device.Commands, x => x[0] is (byte)CommandType.SetFloodScopeKey or (byte)CommandType.SetDefaultFloodScope
            or (byte)CommandType.SendControlData or (byte)CommandType.SendAnonymousRequest);
        Assert.True(device.Disposed);
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM Sessions WHERE EndedUtc IS NOT NULL"));
    }

    [Fact]
    public async Task ProfilesShareFullKeyHistoryButSameBleEndpointWithChangedLastKeyByteDoesNot()
    {
        await using var f = await Fixture.Create();
        Guid? original = null;
        foreach (var kind in new[] { ConnectionTransportKind.Tcp, ConnectionTransportKind.Serial, ConnectionTransportKind.Ble })
        {
            await f.Connect(kind);
            var node = f.Supervisor.Snapshot.NodeId!.Value;
            if (original is { } prior) Assert.Equal(prior, node);
            original = node;
            await f.Supervisor.DisconnectAsync(Token);
        }
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM Nodes"));
        // The emulator repeats identical private text/timestamp: existing private retry dedup
        // merges it across reconnects. Channel text/data retain one row per arrival.
        Assert.Equal(7, f.Scalar("SELECT COUNT(*) FROM Messages"));
        await f.Connect(suffix: 99);
        Assert.NotEqual(original, f.Supervisor.Snapshot.NodeId);
        await f.Supervisor.DisconnectAsync(Token);
        Assert.Equal(2, f.Scalar("SELECT COUNT(*) FROM Nodes"));
        Assert.Equal(10, f.Scalar("SELECT COUNT(*) FROM Messages"));
        Assert.Equal(3, f.Scalar("SELECT COUNT(*) FROM Messages m JOIN Sessions s ON s.Id=m.SessionId WHERE s.NodeId<>$id", original));
        await f.Connect();
        Assert.Equal(original, f.Supervisor.Snapshot.NodeId);
        await f.Supervisor.ShutdownAsync(Token);
        Assert.Equal(12, f.Scalar("SELECT COUNT(*) FROM Messages"));
        Assert.All(f.Devices.Where(d => d.Commands.Count > 0), d => Assert.True(d.Disposed));
    }

    [Theory]
    [InlineData(CommandType.GetContacts)]
    [InlineData(CommandType.SyncNextMessage)]
    public async Task DropDuringSynchronizationEndsSessionAndExplicitRetryUsesFreshGatt(CommandType command)
    {
        await using var f = await Fixture.Create();
        var failed = await f.Connect(drop: command, expectOnline: false);
        await f.Supervisor.DisconnectAsync(Token);
        Assert.True(failed.Disposed);
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM Sessions WHERE EndedUtc IS NOT NULL"));
        await f.Connect();
        await f.Supervisor.ShutdownAsync(Token);
        Assert.Equal(2, f.Devices.Count);
        Assert.Equal(3, f.Scalar("SELECT COUNT(*) FROM Messages"));
        Assert.All(f.Devices, device => Assert.DoesNotContain(device.Commands,
            x => x[0] == (byte)CommandType.SendTextMessage));
    }

    [Fact]
    public async Task CancelOpenDoesNotLeaveSessionOrLateConnection()
    {
        await using var f = await Fixture.Create();
        var device = f.AddDevice();
        device.HoldOpen = true;
        await f.Select(ConnectionTransportKind.Ble);
        await f.Supervisor.ConnectNowAsync(Token);
        await device.Opening.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await f.Supervisor.DisconnectAsync(Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
        await device.Transport.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(ConnectionSupervisorState.Offline, f.Supervisor.Snapshot.State);
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM Sessions WHERE EndedUtc IS NOT NULL"));
        Assert.Empty(device.Commands);
        Assert.True(device.Disposed);
    }

    [Fact]
    public async Task ReceiveOverflowIsTerminalAndCannotSilentlyKeepOnlineSession()
    {
        await using var f = await Fixture.Create();
        var device = await f.Connect();
        for (var index = 0; index < 10000; index++) device.Emit([(byte)PacketType.MessagesWaiting]);
        await Wait(() => f.Supervisor.Snapshot.State is ConnectionSupervisorState.Offline or ConnectionSupervisorState.NeedsAttention);
        await f.Supervisor.DisconnectAsync(Token);
        Assert.Contains(f.Errors, error => error is MeshCoreBleTransportException
            { Reason: MeshCoreBleFailureReason.ReceiveQueueOverflow });
        Assert.True(device.Disposed);
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM Sessions WHERE EndedUtc IS NOT NULL"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateSendLateAckOrDisconnectUsesExistingDeliverySemanticsWithoutReplay(bool disconnect)
    {
        await using var f = await Fixture.Create();
        var device = await f.Connect(reconnect: disconnect);
        device.Server.AutoAcknowledgePrivate = false;
        var owner = f.Supervisor.Snapshot;
        var node = owner.NodeId!.Value;
        var key = Enumerable.Repeat((byte)0xA1, 32).ToArray();
        var draft = new DraftTarget(node, null, ConversationKind.Contact, key);
        await f.Drafts.LoadTextAsync(draft, Token);
        f.Drafts.Update(draft, "BLE16 тест 👋", 1);
        if (disconnect)
        {
            device.DropOnCommand = CommandType.SendTextMessage;
            f.AddDevice(); // The next factory call must use a new GATT session.
        }
        var result = await f.Sender.SendPrivateAsync(new(node, owner.SessionId!.Value, owner.Generation,
            new(ConversationKind.Contact, key), new(draft, "BLE16 тест 👋", 1), new()), cancellationToken: Token);
        if (disconnect)
        {
            try
            {
                await Wait(() => f.Supervisor.Snapshot.Generation > owner.Generation &&
                    f.Supervisor.Snapshot.State == ConnectionSupervisorState.Online);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException($"Reconnect failed: {f.Supervisor.Snapshot}; errors: {string.Join("; ", f.Errors.Select(e => e.ToString()))}");
            }
            Assert.Equal(SendAttemptState.Unknown, (await f.ReadAttempt(node, result.MessageId)).State);
            Assert.Single(device.Commands, x => x[0] == (byte)CommandType.SendTextMessage);
            Assert.DoesNotContain(f.Devices[^1].Commands, x => x[0] == (byte)CommandType.SendTextMessage);
        }
        else
        {
            var transmission = Assert.Single(device.Server.PrivateTransmissions);
            Assert.Equal(SendAttemptState.Accepted, (await f.ReadAttempt(node, result.MessageId)).State);
            await device.Server.SendAcknowledgementAsync(transmission.ExpectedAck);
            await WaitAsync(async () => (await f.ReadAttempt(node, result.MessageId)).State == SendAttemptState.Delivered);
            await device.Server.SendAcknowledgementAsync(transmission.ExpectedAck);
        }
        await f.Supervisor.ShutdownAsync(Token);
        Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(node, result.MessageId, Token));
    }

    [Fact]
    public async Task ChannelSendIsDurableAndOversizedUtf8DraftIsRejectedWithoutWritingGatt()
    {
        await using var f = await Fixture.Create();
        var device = await f.Connect();
        var owner = f.Supervisor.Snapshot;
        var node = owner.NodeId!.Value;
        var fingerprint = System.Security.Cryptography.SHA256.HashData(Enumerable.Repeat((byte)3, 16).ToArray());
        var recipient = Assert.Single(await f.Sender.GetChannelTargetsAsync(node, fingerprint, Token));
        var draft = new DraftTarget(node, null, ConversationKind.Channel, fingerprint);
        await f.Drafts.LoadTextAsync(draft, Token);
        f.Drafts.Update(draft, "BLE16 канал 👋", 1);
        var sent = await f.Sender.SendChannelAsync(new(node, owner.SessionId!.Value, owner.Generation,
            recipient, new(draft, "BLE16 канал 👋", 1), new()), cancellationToken: Token);
        Assert.Equal(SendAttemptState.Accepted, sent.State);
        Assert.Equal("BLE16 канал 👋", device.Server.LastChannelText);
        Assert.Equal(AckExpectation.NotExpected, (await f.ReadAttempt(node, sent.MessageId)).AckExpectation);
        var text = string.Concat(Enumerable.Repeat("👋", 100));
        f.Drafts.Update(draft, text, 2);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Sender.SendChannelAsync(new(node, owner.SessionId!.Value,
            owner.Generation, recipient, new(draft, text, 2), new()), cancellationToken: Token));
        await f.Drafts.FlushAsync(draft, Token);
        Assert.Equal(text, (await f.Storage.Drafts.GetAsync(draft, Token))!.Text);
        await f.Supervisor.ShutdownAsync(Token);
        Assert.Equal(1, device.Server.ChannelSendCount);
        Assert.Single(device.Commands, x => x[0] == (byte)CommandType.SendChannelTextMessage);
    }

    private static Task Wait(Func<bool> ready) => WaitAsync(() => Task.FromResult(ready()));
    private static async Task WaitAsync(Func<Task<bool>> ready)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!await ready()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable, ICompanionTransportFactory, IMeshCoreClientFactory
    {
        private sealed record Paths(string DataDirectory) : IAppPaths
        {
            public string DatabasePath => Path.Combine(DataDirectory, "messenger.db");
            public string BackupsDirectory => Path.Combine(DataDirectory, "backups");
        }
        private readonly Paths _paths = new(Path.Combine(Path.GetTempPath(), "MeshCore-BLE16", Guid.NewGuid().ToString("N")));
        public LocalStorage Storage { get; private set; } = null!;
        public MessageIngestor Ingest { get; private set; } = null!;
        public ConnectionSupervisor Supervisor { get; private set; } = null!;
        public DraftWriteTracker Drafts { get; private set; } = null!;
        public MessageService Sender { get; private set; } = null!;
        public List<BleCompanionEmulator> Devices { get; } = [];
        public ConcurrentQueue<Exception> Errors { get; } = new();
        private ConnectionProfileManager _profiles = null!;
        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            f.Storage = await LocalStorage.OpenAsync(f._paths, Token);
            var time = TimeProvider.System;
            f._profiles = new(f.Storage.ConnectionProfiles, f.Storage.Settings, time);
            f.Ingest = new(f.Storage.IncomingMessages, time);
            var outgoing = new OutgoingAttemptWriteTracker(f.Storage.OutgoingMessages);
            var gateway = new SessionCommandGateway(f.Storage.Directories, f.Storage.ConversationDirectory, f.Storage.OutgoingMessages, outgoing, time);
            var sessions = new CompanionSessionFactory(f, f.Storage.Nodes, f.Storage.Sessions,
                new SessionCompletionTracker(f.Storage.Sessions), time);
            var attempts = new ConnectionAttemptFactory(sessions, new DirectoryService(f.Storage.Directories, time), f.Storage.Directories, f.Ingest, gateway, outgoing);
            f.Supervisor = new(f._profiles, attempts, new ConnectionFailureClassifier(), new SystemReconnectDelay(), new RandomReconnectJitter(), time, outgoing: outgoing);
            f.Drafts = new(f.Storage.Drafts, time);
            f.Sender = new(gateway, f.Storage.OutgoingMessages, f.Storage.Directories, f.Storage.ConversationDirectory,
                f.Drafts, new PassthroughOutgoingTextProcessor(), time, f.Storage.Drafts, new ConversationOperationGuard());
            return f;
        }
        public IMeshCoreTransport Create(ConnectionProfile profile) => profile.Transport == ConnectionTransportKind.Ble
            ? Devices[^1].Transport
            // Serial is substituted at the platform seam: this test checks profile/identity, not a physical COM port.
            : new TcpMeshCoreTransport("127.0.0.1", Devices[^1].Server.Port);
        ICompanionClient IMeshCoreClientFactory.Create(ConnectionProfile profile)
        {
            var client = new MeshCoreClientFactory(this).Create(profile);
            client.BackgroundError += (_, args) => Errors.Enqueue(args.Exception);
            return client;
        }
        public BleCompanionEmulator AddDevice(int capacity = 256)
        {
            var device = new BleCompanionEmulator(capacity); Devices.Add(device); return device;
        }
        public async Task Select(ConnectionTransportKind kind, bool reconnect = false)
        {
            await _profiles.SaveAndSelectAsync(new()
            {
                Name = kind.ToString(), Transport = kind, Reconnect = reconnect,
                TcpHost = kind == ConnectionTransportKind.Tcp ? "localhost" : null,
                TcpPort = kind == ConnectionTransportKind.Tcp ? 5000 : null,
                SerialPortName = kind == ConnectionTransportKind.Serial ? "FAKE" : null,
                BaudRate = kind == ConnectionTransportKind.Serial ? 115200 : null,
                BleDeviceId = kind == ConnectionTransportKind.Ble ? "same-endpoint" : null,
            }, Token);
        }
        public async Task<BleCompanionEmulator> Connect(ConnectionTransportKind kind = ConnectionTransportKind.Ble,
            byte suffix = 31, CommandType? drop = null, bool expectOnline = true, int capacity = 256, bool reconnect = false)
        {
            var device = AddDevice(capacity); device.IdentitySuffix = suffix; device.DropOnCommand = drop;
            await Select(kind, reconnect);
            var prior = Supervisor.Snapshot.Generation;
            await Supervisor.ConnectNowAsync(Token);
            await Wait(() => Supervisor.Snapshot.Generation > prior && Supervisor.Snapshot.State is
                ConnectionSupervisorState.Online or ConnectionSupervisorState.Offline or ConnectionSupervisorState.NeedsAttention);
            if (expectOnline) Assert.Equal(ConnectionSupervisorState.Online, Supervisor.Snapshot.State);
            else Assert.NotEqual(ConnectionSupervisorState.Online, Supervisor.Snapshot.State);
            return device;
        }
        public async Task<OutgoingAttemptSnapshot> ReadAttempt(Guid node, Guid message) =>
            Assert.Single(await Storage.OutgoingMessages.GetAttemptsAsync(node, message, Token));
        public long Scalar(string sql, object? id = null)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
            if (id is not null) command.Parameters.AddWithValue("$id", id.ToString());
            return Convert.ToInt64(command.ExecuteScalar());
        }
        public async ValueTask DisposeAsync()
        {
            await Supervisor.ShutdownAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var device in Devices)
            {
                await device.Transport.DisposeAsync();
                await device.DisposeAsync();
            }
            await Ingest.DisposeAsync(); await Storage.DisposeAsync(); Directory.Delete(_paths.DataDirectory, true);
        }
    }
}
