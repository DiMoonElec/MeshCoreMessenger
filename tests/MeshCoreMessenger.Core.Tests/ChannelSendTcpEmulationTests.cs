using System.Security.Cryptography;
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
    public async Task ProductionChannelSendThroughTcpEmulatedCompanion(bool reject)
    {
        await using var server = new FakeCompanionServer { RejectChannelSend = reject };
        server.Start();
        var paths = new Fixture.Paths(Path.Combine(Path.GetTempPath(), "MeshCore-D5-TCP", Guid.NewGuid().ToString("N")));
        try
        {
            await using var storage = await LocalStorage.OpenAsync(paths, CancellationToken);
            var time = TimeProvider.System;
            var profiles = new ConnectionProfileManager(storage.ConnectionProfiles, storage.Settings, time);
            var profile = new ConnectionProfile
            {
                Id = Guid.NewGuid(),
                Name = "Loopback emulator",
                Transport = ConnectionTransportKind.Tcp,
                TcpHost = "127.0.0.1",
                TcpPort = server.Port,
                Reconnect = false,
                CreatedUtc = time.GetUtcNow(),
                UpdatedUtc = time.GetUtcNow(),
            };
            await storage.ConnectionProfiles.SaveAsync(profile, CancellationToken);
            await profiles.SelectAsync(profile.Id, CancellationToken);
            await using var ingress = new MessageIngestor(storage.IncomingMessages, time);
            var outgoing = new OutgoingAttemptWriteTracker(storage.OutgoingMessages);
            var gateway = new SessionCommandGateway(storage.Directories, storage.ConversationDirectory, storage.OutgoingMessages, outgoing, time);
            var sessions = new CompanionSessionFactory(new MeshCoreClientFactory(), storage.Nodes, storage.Sessions,
                new SessionCompletionTracker(storage.Sessions), time);
            var attempts = new ConnectionAttemptFactory(sessions, new DirectoryService(storage.Directories, time), storage.Directories, ingress, gateway, outgoing);
            await using var supervisor = new ConnectionSupervisor(profiles, attempts, new ConnectionFailureClassifier(),
                new SystemReconnectDelay(), new RandomReconnectJitter(), time, outgoing: outgoing);
            await supervisor.ConnectNowAsync(CancellationToken);
            await Until(() => supervisor.Snapshot.State is ConnectionSupervisorState.Online or ConnectionSupervisorState.NeedsAttention);
            Assert.Equal(ConnectionSupervisorState.Online, supervisor.Snapshot.State);
            var owner = supervisor.Snapshot;
            var node = owner.NodeId!.Value;
            var fingerprint = SHA256.HashData(Enumerable.Repeat((byte)3, 16).ToArray());
            var drafts = new DraftWriteTracker(storage.Drafts, time);
            var sender = new MessageService(gateway, storage.OutgoingMessages, storage.Directories,
                storage.ConversationDirectory, drafts, new PassthroughOutgoingTextProcessor(), time, storage.Drafts, new ConversationOperationGuard());
            var target = Assert.Single(await sender.GetChannelTargetsAsync(node, fingerprint, CancellationToken));
            var draftTarget = new DraftTarget(node, null, ConversationKind.Channel, fingerprint);
            await drafts.LoadTextAsync(draftTarget, CancellationToken);
            drafts.Update(draftTarget, "Тест D5 👋", 1);
            Guid messageId = Guid.Empty;
            storage.OutgoingMessages.MessageCommitted += (_, commit) => { if (commit.Inserted) messageId = commit.MessageId; };
            server.BeforeChannelResponse = async () =>
            {
                var attempt = Assert.Single(await storage.OutgoingMessages.GetAttemptsAsync(node, messageId, CancellationToken));
                Assert.Equal(SendAttemptState.Sending, attempt.State);
                Assert.Equal("Тест D5 👋", (await storage.OutgoingMessages.GetAsync(node, messageId, CancellationToken)).TransmissionText);
            };
            var outcome = await sender.SendChannelAsync(new(node, owner.SessionId!.Value, owner.Generation, target,
                new(draftTarget, "Тест D5 👋", 1), new()), cancellationToken: CancellationToken);
            Assert.Equal(reject ? SendAttemptState.Failed : SendAttemptState.Accepted, outcome.State);
            Assert.Equal(1, server.ChannelSendCount); Assert.Equal((byte)0, server.LastChannelSlot);
            Assert.Equal("Тест D5 👋", server.LastChannelText);
            var final = Assert.Single(await storage.OutgoingMessages.GetAttemptsAsync(node, outcome.MessageId, CancellationToken));
            Assert.Equal(outcome.State, final.State);
            if (!reject) Assert.Equal(AckExpectation.NotExpected, final.AckExpectation);
            await supervisor.ShutdownAsync(CancellationToken);
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken);
            Assert.Equal(1, server.ChannelSendCount);
        }
        finally { Directory.Delete(paths.DataDirectory, true); }
    }
}
