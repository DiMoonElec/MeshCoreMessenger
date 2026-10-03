using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using MeshCoreSharp;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class SessionCommandGatewayTests
{
    [Theory]
    [InlineData("connect")]
    [InlineData("identify")]
    [InlineData("directory")]
    [InlineData("drain")]
    public async Task NoAdmissionBeforeOnlineOrForWrongNode(string phase)
    {
        await using var fixture = await Fixture.CreateAsync();
        var gate = NewGate();
        fixture.Clients.Configure = client => client.StartupPhase = (phase, gate);
        Assert.Throws<InvalidOperationException>(() => fixture.Gateway.Acquire(Guid.NewGuid(), new NodeCommandTarget(), CancellationToken));
        await fixture.Supervisor.ConnectNowAsync(CancellationToken);
        await Until(() => fixture.Clients.Current?.PhaseReached == phase);
        var identified = await fixture.Storage.Nodes.GetByPublicKeyAsync(new byte[32], CancellationToken);
        Assert.Throws<InvalidOperationException>(() => fixture.Gateway.Acquire(identified?.Id ?? Guid.NewGuid(), new NodeCommandTarget(), CancellationToken));
        gate.SetResult();
        await fixture.Online();
        Assert.Throws<InvalidOperationException>(() => fixture.Gateway.Acquire(Guid.NewGuid(), new NodeCommandTarget(), CancellationToken));
        await using var lease = fixture.Acquire();
        Assert.Equal(fixture.Supervisor.Snapshot.SessionId, lease.Owner.SessionId);
        Assert.Equal(fixture.Supervisor.Snapshot.Generation, lease.Owner.Generation);
        Assert.Equal("Node", lease.Owner.SenderName);
    }

    [Fact]
    public async Task StaleLeaseBeforeInvocationHasZeroTxAndDoesNotReopenAfterReconnect()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Connect();
        var old = fixture.Acquire();
        var oldOwner = old.Owner;
        await fixture.Supervisor.DisconnectAsync(CancellationToken);
        await Assert.ThrowsAnyAsync<Exception>(() => old.SendAdvertisementAsync(AdvertisementMode.Flood));
        Assert.Equal(0, fixture.Clients.All.Sum(client => client.Tx));
        await fixture.Connect();
        await Assert.ThrowsAnyAsync<Exception>(() => old.SendAdvertisementAsync(AdvertisementMode.Flood));
        await using var current = fixture.Acquire();
        Assert.NotEqual(oldOwner.SessionId, current.Owner.SessionId);
        Assert.True(current.Owner.Generation > oldOwner.Generation);
        Assert.Equal(oldOwner, old.Owner);
        await old.DisposeAsync();
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("switch")]
    [InlineData("shutdown")]
    public async Task BlockedInvocationIsCanceledAndOldOperationsDrainBeforeReplacement(string action)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Connect();
        var client = fixture.Clients.Current!;
        var gate = NewGate();
        client.AdvertGate = gate;
        client.IgnoreCommandCancellation = true;
        var lease = fixture.Acquire();
        var owner = lease.Owner;
        var command = lease.SendAdvertisementAsync(AdvertisementMode.Flood);
        await Until(() => client.Tx == 1);
        Task stop = action switch
        {
            "disconnect" => fixture.Supervisor.DisconnectAsync(CancellationToken),
            "switch" => fixture.Supervisor.SwitchProfileAsync(fixture.SecondProfile.Id, CancellationToken),
            _ => fixture.Supervisor.ShutdownAsync(CancellationToken),
        };
        await Until(() => lease.Token.IsCancellationRequested);
        Assert.Throws<InvalidOperationException>(() => fixture.Acquire());
        Assert.Single(fixture.Clients.All);
        Assert.False(client.Disposed);
        Assert.Equal(owner, lease.Owner);
        gate.SetResult();
        await command;
        await stop;
        if (action == "switch") await fixture.Online(generationAfter: owner.Generation);
        Assert.True(client.Disposed);
        Assert.Equal(1, client.Tx);
        Assert.True(client.Calls.ToArray().ToList().IndexOf("disconnect") < client.Calls.ToArray().ToList().IndexOf("dispose"));
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task CancellationCallbacksCannotBlockSupervisorControlLoop()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Connect();
        var lease = fixture.Acquire();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var registration = lease.Token.Register(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None); });
        var disconnect = fixture.Supervisor.DisconnectAsync(CancellationToken);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), CancellationToken));
            // Switch command is acknowledged while teardown's cancellation callback is blocked.
            await fixture.Supervisor.SwitchProfileAsync(fixture.SecondProfile.Id, CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken);
            Assert.Single(fixture.Clients.All);
        }
        finally { release.Set(); }
        await disconnect.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task PreparedSendingAndAwaitingAckStopHaveCorrectCrashDistinction()
    {
        foreach (var phase in new[] { "prepared", "sending", "invoked", "ack" })
        {
            await using var fixture = await Fixture.CreateAsync();
            await fixture.Connect();
            var prepared = await fixture.Prepare();
            var lease = fixture.AcquireContact();
            await lease.BindOutgoingAsync(prepared.MessageId, prepared.Attempt.Id);
            if (phase != "prepared") await lease.TransitionAsync(SendAttemptState.Prepared, SendAttemptState.Sending);
            if (phase is "invoked" or "ack") await lease.SendTextAsync();
            if (phase == "ack") await lease.TransitionAsync(SendAttemptState.Sending, SendAttemptState.Accepted,
                AckExpectation.Expected, 42, new byte[] { 1, 2, 3, 4 });
            await fixture.Supervisor.DisconnectAsync(CancellationToken);
            var stored = Assert.Single(await fixture.Storage.OutgoingMessages.GetAttemptsAsync(lease.Owner.NodeId, prepared.MessageId, CancellationToken));
            Assert.Equal(phase switch { "prepared" => SendAttemptState.Prepared, "sending" => SendAttemptState.Failed, _ => SendAttemptState.Unknown }, stored.State);
            Assert.Equal(phase is "invoked" or "ack" ? 1 : 0, fixture.Clients.Current!.Tx);
            Assert.Equal(lease.Owner.SessionId, stored.SessionId);
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task StoredTransmissionAndOneInvocationAreEnforcedAndRecipientIsRevalidated()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Connect();
        var prepared = await fixture.Prepare();
        await using var lease = fixture.AcquireContact();
        await lease.BindOutgoingAsync(prepared.MessageId, prepared.Attempt.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.SendTextAsync());
        await lease.TransitionAsync(SendAttemptState.Prepared, SendAttemptState.Sending);
        await lease.SendTextAsync();
        Assert.Equal("transmission", fixture.Clients.Current!.SentText);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.SendTextAsync());
        Assert.Equal(1, fixture.Clients.Current.Tx);
        var second = await fixture.Prepare();
        await using var stale = fixture.AcquireContact();
        await stale.BindOutgoingAsync(second.MessageId, second.Attempt.Id);
        await stale.TransitionAsync(SendAttemptState.Prepared, SendAttemptState.Sending);
        fixture.Execute("UPDATE Contacts SET PresentOnNode=0;");
        await Assert.ThrowsAsync<InvalidOperationException>(() => stale.SendTextAsync());
        Assert.Equal(1, fixture.Clients.Current.Tx);
    }

    [Fact]
    public async Task ChannelBindingGenerationAndTargetCopiesAreVerified()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Connect();
        var channel = await fixture.ChannelRecipient();
        var prepared = await fixture.Prepare(channel);
        await using var lease = fixture.Gateway.Acquire(fixture.NodeId, new ChannelCommandTarget(channel), CancellationToken);
        await lease.BindOutgoingAsync(prepared.MessageId, prepared.Attempt.Id);
        await lease.TransitionAsync(SendAttemptState.Prepared, SendAttemptState.Sending);
        fixture.Execute("UPDATE ChannelBindings SET UnboundUtc=BoundUtc;");
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.SendChannelTextAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.ClearChannelAsync());
        Assert.Equal(0, fixture.Clients.Current!.Tx);
        var key = fixture.ContactKey.ToArray();
        await using var captured = fixture.Gateway.Acquire(fixture.NodeId, new ContactCommandTarget(key), CancellationToken);
        key[0]++;
        Assert.Equal(fixture.ContactKey, ((ContactCommandTarget)captured.Target).PublicKey.ToArray());
    }

    [Fact]
    public async Task DurableFailurePreservesAcceptedThenCleanupOrderAndPreventsNewClientUntilRetry()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Connect();
        var prepared = await fixture.Prepare();
        var lease = fixture.AcquireContact();
        await lease.BindOutgoingAsync(prepared.MessageId, prepared.Attempt.Id);
        await lease.TransitionAsync(SendAttemptState.Prepared, SendAttemptState.Sending);
        await lease.SendTextAsync();
        fixture.Execute("CREATE TRIGGER RejectStatus BEFORE UPDATE ON SendAttempts BEGIN SELECT RAISE(ABORT,'injected'); END;");
        await Assert.ThrowsAsync<OutgoingPersistenceException>(() => lease.TransitionAsync(SendAttemptState.Sending, SendAttemptState.Accepted,
            AckExpectation.Expected, 42, new byte[] { 1, 2, 3, 4 }));
        await Until(() => fixture.Supervisor.Snapshot.State == ConnectionSupervisorState.NeedsAttention && fixture.Outgoing.PendingCount == 2);
        Assert.ThrowsAny<Exception>(() => fixture.Acquire());
        await fixture.Supervisor.DisconnectAsync(CancellationToken);
        Assert.Equal(2, fixture.Outgoing.PendingCount);
        Assert.Single(fixture.Clients.All);
        await fixture.Supervisor.SwitchProfileAsync(fixture.SecondProfile.Id, CancellationToken);
        await Until(() => fixture.Supervisor.Snapshot.State == ConnectionSupervisorState.NeedsAttention);
        Assert.Single(fixture.Clients.All);
        fixture.Execute("DROP TRIGGER RejectStatus;");
        await fixture.Supervisor.ConnectNowAsync(CancellationToken);
        await fixture.Online(generationAfter: lease.Owner.Generation);
        var final = Assert.Single(await fixture.Storage.OutgoingMessages.GetAttemptsAsync(lease.Owner.NodeId, prepared.MessageId, CancellationToken));
        Assert.Equal(SendAttemptState.Unknown, final.State);
        Assert.NotNull(final.AcceptedUtc);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, final.ExpectedAck!.Value.ToArray());
        Assert.Equal(0, fixture.Outgoing.PendingCount);
        Assert.Equal(1, fixture.Clients.All.Sum(client => client.Tx));
        try { await lease.DisposeAsync(); } catch (OutgoingPersistenceException) { }
    }

    [Fact]
    public async Task CompleteWorkflowAndAckObserverRemainOwnedWhileTeardownWaits()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Connect();
        var prepared = await fixture.Prepare();
        var lease = fixture.AcquireContact();
        var observerStarted = NewGate();
        var observerExit = NewGate();
        var work = lease.RunAsync(async (operation, token) =>
        {
            await operation.BindOutgoingAsync(prepared.MessageId, prepared.Attempt.Id);
            await operation.TransitionAsync(SendAttemptState.Prepared, SendAttemptState.Sending);
            await operation.SendTextAsync();
            await operation.TransitionAsync(SendAttemptState.Sending, SendAttemptState.Accepted, AckExpectation.Expected, 42, new byte[] { 1, 2, 3, 4 });
            var observer = operation.ObserveAsync(async (owned, _) =>
            {
                observerStarted.SetResult();
                await observerExit.Task;
                await owned.TransitionAsync(SendAttemptState.Accepted, SendAttemptState.Unknown);
            });
            await observer;
            return true;
        });
        await observerStarted.Task.WaitAsync(CancellationToken);
        var oldGeneration = lease.Owner.Generation;
        await fixture.Supervisor.SwitchProfileAsync(fixture.SecondProfile.Id, CancellationToken);
        await Until(() => lease.Token.IsCancellationRequested);
        Assert.Single(fixture.Clients.All);
        observerExit.SetResult();
        await work;
        await fixture.Online(generationAfter: oldGeneration);
        Assert.Equal(SendAttemptState.Unknown, Assert.Single(await fixture.Storage.OutgoingMessages.GetAttemptsAsync(lease.Owner.NodeId, prepared.MessageId, CancellationToken)).State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lease.TransitionAsync(SendAttemptState.Unknown, SendAttemptState.Failed));
    }

    [Fact]
    public async Task IncomingEventsDuringDisconnectCommitBeforeReplacement()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Connect();
        var client = fixture.Clients.Current!;
        var node = fixture.NodeId;
        client.DisconnectAction = () => client.EmitMessage(new ContactMessage(fixture.ContactKey[..6], 0, MessageTextType.Plain,
            DateTimeOffset.UtcNow, "at disconnect", null, null));
        await fixture.Supervisor.SwitchProfileAsync(fixture.SecondProfile.Id, CancellationToken);
        await fixture.Online(generationAfter: 1);
        var conversations = await fixture.Storage.History.GetConversationsAsync(node, 10, CancellationToken);
        var conversation = Assert.Single(conversations);
        Assert.Equal("at disconnect", Assert.Single(await fixture.Storage.History.GetMessagesAsync(node, conversation.Id, null, 10, CancellationToken)).Text);
    }

    [Fact]
    public async Task RetryWaitingRejectsAdmissionAndReconnectNeverReplaysCommands()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Connect();
        var node = fixture.NodeId;
        await using var lease = fixture.Acquire();
        fixture.Clients.Current!.Fail();
        await Until(() => fixture.Supervisor.Snapshot.State == ConnectionSupervisorState.RetryWaiting);
        Assert.Throws<InvalidOperationException>(() => fixture.Gateway.Acquire(node, new NodeCommandTarget(), CancellationToken));
        await fixture.Supervisor.ConnectNowAsync(CancellationToken);
        await fixture.Online(generationAfter: lease.Owner.Generation);
        Assert.Equal(0, fixture.Clients.All.Sum(client => client.Tx));
    }

    [Fact]
    public async Task TypedMutationAdaptersUseCapturedTargetsAndNoWorkflowIsImplicit()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Connect();
        await using var contact = fixture.AcquireContact();
        var configuration = new ContactConfiguration(fixture.ContactKey, AdvertisementType.Chat, 0, 255, new byte[64], "Peer", 0, 0, 0);
        await contact.AddOrUpdateContactAsync(configuration);
        await contact.RemoveContactAsync();
        await contact.GetContactsAsync();
        var slot = fixture.Gateway.Acquire(fixture.NodeId, new ChannelSlotCommandTarget(9), CancellationToken);
        await slot.SetChannelAsync("new", fixture.Clients.Secret);
        await slot.ClearChannelAsync();
        await slot.GetChannelsAsync();
        await slot.DisposeAsync();
        var client = fixture.Clients.Current!;
        Assert.Equal(["add", "remove", "set:9", "clear:9"], client.Mutations.ToArray());
        Assert.Equal(fixture.ContactKey, client.MutatedContactKey);
        Assert.Equal(fixture.Clients.Secret, client.MutatedSecret);
        Assert.Equal(4, client.Tx);
        Assert.False(fixture.Outgoing.IsPaused);
    }

    [Fact]
    public async Task PreparingWithDiskFailureClosesAdmissionBeforeAnyTextApi()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Connect();
        var lease = fixture.AcquireContact();
        fixture.Execute("CREATE TRIGGER RejectPrepare BEFORE INSERT ON Messages BEGIN SELECT RAISE(ABORT,'injected'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => lease.RunAsync(async (_, _) => await fixture.Prepare()));
        await Until(() => fixture.Supervisor.Snapshot.State == ConnectionSupervisorState.NeedsAttention);
        Assert.True(fixture.Outgoing.IsPaused);
        Assert.Equal(0, fixture.Clients.Current!.Tx);
        Assert.Empty(await fixture.Storage.History.GetMessagesAsync(lease.Owner.NodeId,
            (await fixture.Storage.History.GetConversationsAsync(lease.Owner.NodeId, 10, CancellationToken)).Single().Id, null, 10, CancellationToken));
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task ClosingDuringBlockedSendingCommitPreventsApiAndStillCommitsFinalState()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Connect();
        var prepared = await fixture.Prepare();
        var lease = fixture.AcquireContact();
        await lease.BindOutgoingAsync(prepared.MessageId, prepared.Attempt.Id);
        using var blocker = SqliteDatabase.CreateConnection(fixture.AppPaths.DatabasePath, SqliteOpenMode.ReadWrite);
        blocker.Open();
        using var transaction = blocker.BeginTransaction();
        var workflow = lease.RunAsync(async (owned, _) =>
        {
            await owned.TransitionAsync(SendAttemptState.Prepared, SendAttemptState.Sending);
            return await owned.SendTextAsync();
        });
        await Until(() => fixture.Outgoing.PendingCount == 1);
        var disconnect = fixture.Supervisor.DisconnectAsync(CancellationToken);
        try
        {
            await Until(() => lease.Token.IsCancellationRequested);
            Assert.Equal(0, fixture.Clients.Current!.Tx);
        }
        finally { transaction.Rollback(); }
        await Assert.ThrowsAnyAsync<Exception>(() => workflow);
        await disconnect.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        var final = Assert.Single(await fixture.Storage.OutgoingMessages.GetAttemptsAsync(lease.Owner.NodeId, prepared.MessageId, CancellationToken));
        Assert.Equal(SendAttemptState.Failed, final.State);
        Assert.Equal(0, fixture.Clients.Current!.Tx);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task OneAttemptCannotBeBoundByTwoLeasesAndWrongRecipientHasZeroTx()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Connect();
        var prepared = await fixture.Prepare();
        await using var first = fixture.AcquireContact();
        await using var second = fixture.AcquireContact();
        await first.BindOutgoingAsync(prepared.MessageId, prepared.Attempt.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.BindOutgoingAsync(prepared.MessageId, prepared.Attempt.Id));
        await using var wrong = fixture.Gateway.Acquire(fixture.NodeId, new ContactCommandTarget(new byte[32]), CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => wrong.BindOutgoingAsync(prepared.MessageId, prepared.Attempt.Id));
        Assert.Equal(0, fixture.Clients.Current!.Tx);
    }

    [Fact]
    public async Task ThrowingCancellationCallbackStillReleasesAllLeasesAndTransport()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Connect();
        var lease = fixture.Acquire();
        using var registration = lease.Token.Register(() => throw new InvalidOperationException("injected callback"));
        await fixture.Supervisor.DisconnectAsync(CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        Assert.True(fixture.Clients.Current!.Disposed);
        await lease.DisposeAsync();
        Assert.Equal(0, fixture.Clients.Current.Tx);
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class Fixture : IAsyncDisposable
    {
        public sealed record Paths(string DataDirectory) : IAppPaths
        {
            public string DatabasePath => Path.Combine(DataDirectory, "messenger.db");
            public string BackupsDirectory => Path.Combine(DataDirectory, "backups");
        }
        public required Paths AppPaths { get; init; }
        public required LocalStorage Storage { get; init; }
        public required MessageIngestor Ingestor { get; init; }
        public required SessionCommandGateway Gateway { get; init; }
        public required OutgoingAttemptWriteTracker Outgoing { get; init; }
        public required ConnectionSupervisor Supervisor { get; init; }
        public required ClientFactory Clients { get; init; }
        public required ConnectionProfile SecondProfile { get; init; }
        public byte[] ContactKey => Clients.ContactKey;
        public Guid NodeId => Supervisor.Snapshot.NodeId!.Value;
        public static async Task<Fixture> CreateAsync()
        {
            var paths = new Paths(Path.Combine(Path.GetTempPath(), "MeshCore-D4", Guid.NewGuid().ToString("N")));
            var storage = await LocalStorage.OpenAsync(paths, CancellationToken);
            var time = TimeProvider.System;
            var profiles = new ConnectionProfileManager(storage.ConnectionProfiles, storage.Settings, time);
            var profile = new ConnectionProfile
            {
                Id = Guid.NewGuid(),
                Name = "Test",
                Transport = ConnectionTransportKind.Tcp,
                TcpHost = "localhost",
                TcpPort = 5000,
                Reconnect = true,
                CreatedUtc = time.GetUtcNow(),
                UpdatedUtc = time.GetUtcNow()
            };
            var second = profile with { Id = Guid.NewGuid(), Name = "Second" };
            await storage.ConnectionProfiles.SaveAsync(profile, CancellationToken);
            await storage.ConnectionProfiles.SaveAsync(second, CancellationToken);
            await profiles.SelectAsync(profile.Id, CancellationToken);
            var clients = new ClientFactory();
            var ingestor = new MessageIngestor(storage.IncomingMessages, time);
            var outgoing = new OutgoingAttemptWriteTracker(storage.OutgoingMessages);
            var gateway = new SessionCommandGateway(storage.Directories, storage.ConversationDirectory, storage.OutgoingMessages, outgoing, time);
            var sessions = new CompanionSessionFactory(clients, storage.Nodes, storage.Sessions, new SessionCompletionTracker(storage.Sessions), time);
            var attempts = new ConnectionAttemptFactory(sessions, new DirectoryService(storage.Directories, time), storage.Directories, ingestor, gateway, outgoing);
            var supervisor = new ConnectionSupervisor(profiles, attempts, new ConnectionFailureClassifier(), new SystemReconnectDelay(), new RandomReconnectJitter(), time, outgoing: outgoing);
            return new() { AppPaths = paths, Storage = storage, Ingestor = ingestor, Gateway = gateway, Outgoing = outgoing, Supervisor = supervisor, Clients = clients, SecondProfile = second };
        }
        public Task Online(long generationAfter = 0) => Until(() => Supervisor.Snapshot.State == ConnectionSupervisorState.Online && Supervisor.Snapshot.Generation > generationAfter);
        public async Task Connect() { await Supervisor.ConnectNowAsync(CancellationToken); await Online(); }
        public SessionCommandLease Acquire() => Gateway.Acquire(NodeId, new NodeCommandTarget(), CancellationToken);
        public SessionCommandLease AcquireContact() => Gateway.Acquire(NodeId, new ContactCommandTarget(ContactKey), CancellationToken);
        public async Task<OutgoingRecipient> ChannelRecipient()
        {
            var binding = (await Storage.Directories.GetActiveChannelBindingAsync(NodeId, 7, CancellationToken))!;
            return new(ConversationKind.Channel, SHA256.HashData(Clients.Secret), binding.Id, binding.Slot, binding.Generation);
        }
        public async Task<PreparedOutgoingMessage> Prepare(OutgoingRecipient? recipient = null)
        {
            recipient ??= new(ConversationKind.Contact, ContactKey);
            var draft = await Storage.Drafts.SaveAsync(new(NodeId, null, recipient.Kind, recipient.Identity.ToArray()), "original", DateTimeOffset.UtcNow, CancellationToken);
            return await Storage.OutgoingMessages.PrepareAsync(new(Guid.NewGuid(), NodeId, Supervisor.Snapshot.SessionId!.Value, draft!.ConversationId,
                recipient, "original", "transmission", 160, DateTimeOffset.UtcNow), CancellationToken);
        }
        public void Execute(string sql) { using var c = SqliteDatabase.CreateConnection(AppPaths.DatabasePath, SqliteOpenMode.ReadWrite); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        public async ValueTask DisposeAsync()
        {
            await Supervisor.ShutdownAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            await Ingestor.DisposeAsync();
            await Storage.DisposeAsync();
            Directory.Delete(AppPaths.DataDirectory, true);
        }
    }

    private sealed class ClientFactory : IMeshCoreClientFactory
    {
        public byte[] ContactKey { get; } = Enumerable.Repeat((byte)2, 32).ToArray();
        public byte[] Secret { get; } = Enumerable.Repeat((byte)3, 16).ToArray();
        public ConcurrentQueue<Client> All { get; } = new();
        public Client? Current => All.LastOrDefault();
        public Action<Client>? Configure { get; set; }
        public ICompanionClient Create(ConnectionProfile profile)
        {
            var client = new Client(ContactKey, Secret);
            Configure?.Invoke(client);
            All.Enqueue(client);
            return client;
        }
    }

    private sealed class Client(byte[] contactKey, byte[] secret) : ICompanionClient
    {
        public MeshCoreConnectionState State { get; private set; }
        public bool IsConnected => State == MeshCoreConnectionState.Connected;
        public bool IsStarted { get; private set; }
        public ConcurrentQueue<string> Calls { get; } = new();
        public (string Name, TaskCompletionSource Gate)? StartupPhase { get; set; }
        public string? PhaseReached { get; private set; }
        public TaskCompletionSource? AdvertGate { get; set; }
        public bool IgnoreCommandCancellation { get; set; }
        public Action? DisconnectAction { get; set; }
        public int Tx;
        public bool Disposed { get; private set; }
        public string? SentText { get; private set; }
        public event EventHandler<MeshCoreConnectionStateChangedEventArgs>? ConnectionStateChanged;
        public void Fail()
        {
            var previous = State;
            State = MeshCoreConnectionState.Faulted;
            ConnectionStateChanged?.Invoke(this, new(previous, State));
        }
        public ConcurrentQueue<string> Mutations { get; } = new();
        public byte[]? MutatedContactKey { get; private set; }
        public byte[]? MutatedSecret { get; private set; }
        public Task AddOrUpdateContactAsync(ContactConfiguration contact, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref Tx);
            MutatedContactKey = contact.PublicKey.ToArray(); Mutations.Enqueue("add"); return Task.CompletedTask;
        }
        public Task RemoveContactAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref Tx);
            MutatedContactKey = key.ToArray(); Mutations.Enqueue("remove"); return Task.CompletedTask;
        }
        public Task SetChannelAsync(byte slot, string name, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref Tx);
            MutatedSecret = key.ToArray(); Mutations.Enqueue($"set:{slot}"); return Task.CompletedTask;
        }
        public Task ClearChannelAsync(byte slot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref Tx);
            Mutations.Enqueue($"clear:{slot}"); return Task.CompletedTask;
        }
        public event EventHandler<MeshCoreClientErrorEventArgs>? BackgroundError { add { } remove { } }
        public event EventHandler<CompanionPacketEventArgs>? PacketReceived { add { } remove { } }
        public event EventHandler<CompanionPacketEventArgs>? PushPacketReceived { add { } remove { } }
        public event EventHandler<CompanionPacketEventArgs>? UnhandledPacketReceived { add { } remove { } }
        public event EventHandler<AdvertisementReceivedEventArgs>? AdvertisementReceived { add { } remove { } }
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public void EmitMessage(ReceivedMessage message) => MessageReceived?.Invoke(this, new(message));
        private async Task Phase(string name, CancellationToken token)
        {
            PhaseReached = name;
            if (StartupPhase is { } phase && phase.Name == name) await phase.Gate.Task.WaitAsync(token);
        }
        public async Task ConnectAsync(CancellationToken cancellationToken = default) { await Phase("connect", cancellationToken); State = MeshCoreConnectionState.Connected; }
        public async Task<SelfInfo> StartAsync(CancellationToken cancellationToken = default)
        {
            await Phase("identify", cancellationToken); IsStarted = true;
            return new(AdvertisementType.Chat, 20, 22, new byte[32], 0, 0, 0, 0, 0, false, 868, 125, 7, 5, "Node");
        }
        public async Task<IReadOnlyList<Contact>> GetContactsAsync(CancellationToken cancellationToken = default)
        {
            await Phase("directory", cancellationToken);
            return [new Contact(contactKey, AdvertisementType.Chat, 0, 255, new byte[64], "Peer", 0, 0, 0, 0)];
        }
        public Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ChannelInfo>>([new(7, "channel", secret)]);
        public async Task DrainMessagesAsync(CancellationToken cancellationToken = default) { await Phase("drain", cancellationToken); }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) { Calls.Enqueue("disconnect"); DisconnectAction?.Invoke(); State = MeshCoreConnectionState.Disconnected; return Task.CompletedTask; }
        public Task FlushEventsAsync(CancellationToken cancellationToken = default) { Calls.Enqueue("flush"); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Calls.Enqueue("dispose"); Disposed = true; return ValueTask.CompletedTask; }
        public async Task SendAdvertisementAsync(AdvertisementMode mode, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Tx);
            if (AdvertGate is { } gate) await gate.Task.WaitAsync(IgnoreCommandCancellation ? CancellationToken.None : cancellationToken);
        }
        public Task<TextMessageSendResult> SendTextAsync(ReadOnlyMemory<byte> key, string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref Tx); SentText = text;
            return Task.FromResult(new TextMessageSendResult(42, new(false, 1, 1000), Task.FromResult(new MessageDeliveryResult(MessageDeliveryStatus.NotExpected, null))));
        }
        public Task<ChannelMessageSendResult> SendChannelTextAsync(byte slot, string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref Tx); SentText = text;
            return Task.FromResult(new ChannelMessageSendResult(slot, 42));
        }
    }
}
