using System.Buffers.Binary;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TcpTwoIdenticalPrivateMessagesMatchProtocolTagsWithReverseDuplicateAndUnrelatedAcks(bool differentRecipients)
    {
        await using var f = await TcpPrivateFixture.CreateAsync(autoAck: false);
        var a = await f.Send("Одинаковый текст", 0xA1, 1);
        var b = await f.Send("Одинаковый текст", differentRecipients ? (byte)0xB2 : (byte)0xA1, 2);
        var wire = f.Server.PrivateTransmissions.ToArray();
        Assert.Equal(2, wire.Length);
        Assert.True(wire[1].Timestamp > wire[0].Timestamp);
        Assert.NotEqual(wire[0].ExpectedAck, wire[1].ExpectedAck);
        Assert.All(wire[0].RecipientPrefix, x => Assert.Equal((byte)0xA1, x));
        Assert.All(wire[1].RecipientPrefix, x => Assert.Equal(differentRecipients ? (byte)0xB2 : (byte)0xA1, x));
        Assert.Equal(wire[0].ExpectedAck, BinaryPrimitives.ReadUInt32LittleEndian((await f.Read(a.MessageId)).ExpectedAck!.Value.Span));
        await f.Server.SendAcknowledgementAsync(0xDEADBEEF, 999);
        await f.Server.SendAcknowledgementAsync(wire[1].ExpectedAck, 222);
        await f.WaitState(b.MessageId, SendAttemptState.Delivered);
        Assert.Equal(SendAttemptState.Accepted, (await f.Read(a.MessageId)).State);
        await f.Server.SendAcknowledgementAsync(wire[1].ExpectedAck, 444);
        await f.Server.SendAcknowledgementAsync(wire[0].ExpectedAck, 111);
        await f.WaitState(a.MessageId, SendAttemptState.Delivered);
        Assert.Equal(111, (await f.Read(a.MessageId)).RoundTripMilliseconds);
        Assert.Equal(222, (await f.Read(b.MessageId)).RoundTripMilliseconds);
        Assert.Equal(2, f.Server.PrivateTransmissions.Count);
        Assert.Equal(ConnectionSupervisorState.Online, f.Supervisor.Snapshot.State);
    }

    [Fact]
    public async Task TcpDuplicateExpectedTagMakesBothPendingDeliveriesUnknownInsteadOfMisattributingAck()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(autoAck: false);
        f.Server.PrivateAckTag = _ => 0x12345678;
        var first = await f.Send("Первое", 0xA1, 1);
        var second = await f.Send("Второе", 0xA1, 2);
        await f.WaitState(first.MessageId, SendAttemptState.Unknown);
        await f.WaitState(second.MessageId, SendAttemptState.Unknown);
        await f.Server.SendAcknowledgementAsync(0x12345678);
        await Task.Delay(25, CancellationToken);
        Assert.Equal(SendAttemptState.Unknown, (await f.Read(first.MessageId)).State);
        Assert.Equal(SendAttemptState.Unknown, (await f.Read(second.MessageId)).State);
        Assert.Equal(2, f.Server.PrivateTransmissions.Count);
    }

    [Theory]
    [InlineData("ack", SendAttemptState.Delivered)]
    [InlineData("timeout", SendAttemptState.Unconfirmed)]
    [InlineData("notExpected", SendAttemptState.Accepted)]
    [InlineData("reject", SendAttemptState.Failed)]
    [InlineData("shutdown", SendAttemptState.Unknown)]
    public async Task ProductionPrivateSendThroughTcpHasHonestDeliveryOutcomes(string mode, SendAttemptState expected)
    {
        await using var f = await TcpPrivateFixture.CreateAsync(autoAck: mode == "ack", ackTimeout: mode == "timeout" ? 100 : 5000);
        f.Server.RejectPrivateSend = mode == "reject";
        if (mode == "notExpected") f.Server.PrivateAckTag = _ => 0;
        var generation = f.Supervisor.Snapshot.Generation;
        var commits = new System.Collections.Concurrent.ConcurrentQueue<Guid>();
        f.Storage.OutgoingMessages.MessageCommitted += (_, c) => { if (c.Inserted) commits.Enqueue(c.MessageId); };
        f.Server.BeforePrivateResponse = async () =>
        {
            var message = Assert.Single(commits);
            Assert.Equal(SendAttemptState.Sending, (await f.Read(message)).State);
            Assert.Equal("Тест D6 👋", (await f.Storage.OutgoingMessages.GetAsync(f.Node, message, CancellationToken)).TransmissionText);
        };
        var outcome = await f.Send("Тест D6 👋", 0xA1, 1);
        if (mode == "shutdown") await f.Supervisor.ShutdownAsync(CancellationToken);
        await f.WaitState(outcome.MessageId, expected);
        var attempt = await f.Read(outcome.MessageId);
        Assert.Equal(expected, attempt.State);
        Assert.Single(f.Server.PrivateTransmissions);
        if (mode == "notExpected") Assert.Equal(AckExpectation.NotExpected, attempt.AckExpectation);
        if (mode == "timeout")
        {
            Assert.Equal(generation, f.Supervisor.Snapshot.Generation);
            Assert.Equal(ConnectionSupervisorState.Online, f.Supervisor.Snapshot.State);
        }
        await f.Supervisor.ShutdownAsync(CancellationToken);
        await f.Server.Completion.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken);
        Assert.Single(f.Server.PrivateTransmissions);
    }

    [Fact]
    public async Task TcpHistoryClearWaitsForPrivateAckThenDeletesWithoutWireCommands()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(autoAck: false);
        var sent = await f.Send("Clear after ACK", 0xA1, 1);
        var stored = await f.Storage.OutgoingMessages.GetAsync(f.Node, sent.MessageId, CancellationToken);
        Assert.NotNull(await f.Clear.GetUnavailableReasonAsync(f.Node, stored.ConversationId, CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Clear.ClearAsync(f.Node, stored.ConversationId, CancellationToken));
        var wire = Assert.Single(f.Server.PrivateTransmissions);
        await f.Server.SendAcknowledgementAsync(wire.ExpectedAck);
        await f.WaitState(sent.MessageId, SendAttemptState.Delivered);
        await Until(() => !f.Operations.IsBusy(f.Node, ConversationKind.Contact, stored.Recipient.Identity));
        Assert.Null(await f.Clear.GetUnavailableReasonAsync(f.Node, stored.ConversationId, CancellationToken));
        var count = (await f.Storage.History.GetMessagesAsync(f.Node, stored.ConversationId, null, 50, CancellationToken)).Count;
        Assert.Equal(count, (await f.Clear.ClearAsync(f.Node, stored.ConversationId, CancellationToken)).DeletedCount);
        Assert.Single(f.Server.PrivateTransmissions);
        Assert.Equal(ConnectionSupervisorState.Online, f.Supervisor.Snapshot.State);
    }

    private sealed class TcpPrivateFixture : IAsyncDisposable
    {
        public required FakeCompanionServer Server { get; init; }
        public required Fixture.Paths Paths { get; init; }
        public required LocalStorage Storage { get; init; }
        public required MessageIngestor Ingress { get; init; }
        public required ConnectionSupervisor Supervisor { get; init; }
        public required MessageService Sender { get; init; }
        public required ConversationOperationGuard Operations { get; init; }
        public required HistoryClearService Clear { get; init; }
        public required ContactRouteService Routes { get; init; }
        public required DraftWriteTracker Drafts { get; init; }
        public required Guid Node { get; init; }
        public static async Task<TcpPrivateFixture> CreateAsync(bool autoAck, int ackTimeout = 5000, byte initialRoute = 0xFF)
        {
            var server = new FakeCompanionServer { AutoAcknowledgePrivate = autoAck };
            server.ContactRoutes[0xA1] = initialRoute;
            server.Start();
            var paths = new Fixture.Paths(Path.Combine(Path.GetTempPath(), "MeshCore-D6-TCP", Guid.NewGuid().ToString("N")));
            var storage = await LocalStorage.OpenAsync(paths, CancellationToken);
            var time = TimeProvider.System;
            var profiles = new ConnectionProfileManager(storage.ConnectionProfiles, storage.Settings, time);
            var profile = new ConnectionProfile
            {
                Id = Guid.NewGuid(), Name = "Loopback private emulator", Transport = ConnectionTransportKind.Tcp,
                TcpHost = "127.0.0.1", TcpPort = server.Port, Reconnect = false,
                AcknowledgementTimeoutMilliseconds = ackTimeout, CreatedUtc = time.GetUtcNow(), UpdatedUtc = time.GetUtcNow(),
            };
            await storage.ConnectionProfiles.SaveAsync(profile, CancellationToken);
            await profiles.SelectAsync(profile.Id, CancellationToken);
            var ingress = new MessageIngestor(storage.IncomingMessages, time);
            var outgoing = new OutgoingAttemptWriteTracker(storage.OutgoingMessages);
            var gateway = new SessionCommandGateway(storage.Directories, storage.ConversationDirectory, storage.OutgoingMessages, outgoing, time);
            var sessions = new CompanionSessionFactory(new MeshCoreClientFactory(), storage.Nodes, storage.Sessions,
                new SessionCompletionTracker(storage.Sessions), time);
            var attempts = new ConnectionAttemptFactory(sessions, new DirectoryService(storage.Directories, time), storage.Directories, ingress, gateway, outgoing);
            var supervisor = new ConnectionSupervisor(profiles, attempts, new ConnectionFailureClassifier(),
                new SystemReconnectDelay(), new RandomReconnectJitter(), time, outgoing: outgoing);
            await supervisor.ConnectNowAsync(CancellationToken);
            await Until(() => supervisor.Snapshot.State is ConnectionSupervisorState.Online or ConnectionSupervisorState.NeedsAttention);
            Assert.Equal(ConnectionSupervisorState.Online, supervisor.Snapshot.State);
            var drafts = new DraftWriteTracker(storage.Drafts, time);
            var operations = new ConversationOperationGuard();
            var clear = new HistoryClearService(storage.HistoryClear, operations, new(storage.ReadStates), outgoing);
            var sender = new MessageService(gateway, storage.OutgoingMessages, storage.Directories,
                storage.ConversationDirectory, drafts, new PassthroughOutgoingTextProcessor(), time, storage.Drafts, operations);
            return new() { Server = server, Paths = paths, Storage = storage, Ingress = ingress, Supervisor = supervisor,
                Sender = sender, Operations = operations, Clear = clear, Routes = new(gateway, storage.ConversationDirectory, storage.Directories, operations, time), Drafts = drafts, Node = supervisor.Snapshot.NodeId!.Value };
        }
        public async Task<PrivateSendOutcome> Send(string text, byte peer, long revision)
        {
            var key = Enumerable.Repeat(peer, 32).ToArray();
            var target = new DraftTarget(Node, null, ConversationKind.Contact, key);
            await Drafts.LoadTextAsync(target, CancellationToken); Drafts.Update(target, text, revision);
            var owner = Supervisor.Snapshot;
            return await Sender.SendPrivateAsync(new(Node, owner.SessionId!.Value, owner.Generation,
                new(ConversationKind.Contact, key), new(target, text, revision), new()), cancellationToken: CancellationToken);
        }
        public async Task<OutgoingAttemptSnapshot> Read(Guid message) =>
            Assert.Single(await Storage.OutgoingMessages.GetAttemptsAsync(Node, message, CancellationToken));
        public async Task WaitState(Guid message, SendAttemptState state)
        {
            var stopAt = DateTime.UtcNow.AddSeconds(5);
            while ((await Read(message)).State != state)
            {
                if (DateTime.UtcNow > stopAt) throw new TimeoutException($"Expected {state}.");
                await Task.Delay(10, CancellationToken);
            }
        }
        public async ValueTask DisposeAsync()
        {
            await Supervisor.ShutdownAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            await Server.DisposeAsync(); await Ingress.DisposeAsync(); await Storage.DisposeAsync();
            Directory.Delete(Paths.DataDirectory, true);
        }
    }
}
