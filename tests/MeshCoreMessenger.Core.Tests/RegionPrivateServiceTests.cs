using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using System.Text;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateCleanupFailureDoesNotReplayAndSurvivesAckOrTimeout(bool confirmed)
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        var coordinator = new PrivateDeliveryCoordinator(f.Storage.OutgoingMessages, f.Outgoing, TimeProvider.System, f.Storage.Directories);
        var sender = new MessageService(f.Gateway, f.Storage.OutgoingMessages, f.Storage.Directories, f.Storage.ConversationDirectory,
            drafts, new PassthroughOutgoingTextProcessor(), TimeProvider.System, f.Storage.Drafts, f.Operations, coordinator, f.Storage.Regions);
        var delivery = DeliveryGate();
        f.Clients.Current!.CapturedPrivateSendAction = (timestamp, _, _) => Task.FromResult(new TextMessageSendResult(timestamp, new(true, 42, 100), delivery.Task)
        { ScopeCleanupError = new IOException("RESET failed") });
        var result = await sender.SendPrivateAsync(await PrivateRequest(f, drafts), cancellationToken: CancellationToken);
        await WaitState(f, f.NodeId, result.MessageId, SendAttemptState.Accepted);
        if (confirmed)
        {
            await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(new(f.NodeId, f.Supervisor.Snapshot.SessionId!.Value, 42, 1, DateTimeOffset.UtcNow), CancellationToken);
            delivery.TrySetResult(new(MessageDeliveryStatus.Confirmed, new(42, 1)));
        }
        else delivery.TrySetResult(new(MessageDeliveryStatus.TimedOut, null));
        await Until(() => coordinator.PendingCount == 0);
        var attempt = Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, result.MessageId, CancellationToken));
        Assert.Equal(confirmed ? SendAttemptState.Delivered : SendAttemptState.Unconfirmed, attempt.State);
        Assert.Equal("ScopeCleanupFailed", attempt.ErrorCode); Assert.Equal(1, f.Clients.Current.Tx);
    }

    private static byte[] DefaultRegionFrame(RegionDefinition definition)
    {
        var frame = new byte[48]; frame[0] = (byte)PacketType.DefaultFloodScope;
        Encoding.UTF8.GetBytes(definition.Name).CopyTo(frame, 1); definition.Key.ToArray().CopyTo(frame, 32); return frame;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateCyclePinsAcceptedDefaultAcrossRetriesAndKnownToFlood(bool known)
    {
        var a = RegionDefinition.Public("A"); var b = RegionDefinition.Public("B");
        await using var f = await TcpPrivateFixture.CreateAsync(false, 250, known ? (byte)0 : byte.MaxValue, true,
            server => { server.FirmwareProtocolVersion = 14; server.DefaultFloodScopeResponse = DefaultRegionFrame(a); });
        var result = await f.Send("REGION20 scope", 0xA1, 1);
        await Until(() => f.Server.PrivateTransmissions.Count == 1);
        var service = new RegionService(f.Storage.Regions, TimeProvider.System, f.Gateway);
        await using var owner = f.Gateway.Acquire(f.Node, new NodeCommandTarget(), CancellationToken);
        Assert.Equal(RegionDefaultChangeOutcome.Applied, (await service.SetNodeDefaultAsync(owner.Owner, b, CancellationToken)).Outcome);
        await WaitCycle(f, result.MessageId, PrivateDeliveryState.Unconfirmed);
        var attempts = await f.Storage.OutgoingMessages.GetAttemptsAsync(f.Node, result.MessageId, CancellationToken);
        Assert.Equal(known ? 5 : 3, attempts.Count);
        Assert.Equal(known ? new byte[] { 0, 1, 2, 0, 1 } : [0, 1, 2], f.Server.PrivateTransmissions.Select(p => p.Attempt));
        Assert.All(f.Server.PrivateTransmissions, p => Assert.Equal(a.Key.ToArray(), p.ScopeKey.ToArray()));
        foreach (var attempt in attempts)
        {
            var scope = await f.Storage.OutgoingMessages.GetAttemptScopeAsync(f.Node, attempt.Id, CancellationToken);
            Assert.Equal(a.Key, scope.Key); Assert.Equal("A", scope.Name);
        }
        Assert.Equal("B", f.Gateway.GetDefaultRegion(f.Node)!.Value.Name);
    }

    [Fact]
    public async Task LateAckStopsScopedJobAndScopeSetFailureHasNoTextTransmission()
    {
        var a = RegionDefinition.Public("A");
        await using var f = await TcpPrivateFixture.CreateAsync(false, 250, autoRetries: true,
            configure: server => { server.FirmwareProtocolVersion = 14; server.DefaultFloodScopeResponse = DefaultRegionFrame(a); });
        var first = await f.Send("REGION20 ACK", 0xA1, 1);
        await Until(() => f.Server.PrivateTransmissions.Count >= 2);
        var firstWire = f.Server.PrivateTransmissions.First();
        await f.Server.SendAcknowledgementAsync(firstWire.ExpectedAck);
        await WaitCycle(f, first.MessageId, PrivateDeliveryState.Delivered);
        await Until(() => f.Deliveries!.PendingCount == 0);
        Assert.Equal(2, f.Server.PrivateTransmissions.Count);
        f.Server.RejectTemporaryScopeSet = true;
        var next = await f.Send("REGION20 SET refusal", 0xA1, 2);
        await Until(() => f.Deliveries!.PendingCount == 0);
        var attempt = Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.Node, next.MessageId, CancellationToken));
        Assert.Equal(SendAttemptState.Failed, attempt.State); Assert.Equal(2, f.Server.PrivateTransmissions.Count);
    }

    [Fact]
    public async Task LegacyPrivateCyclePreservesOldWirePathWithUnknownProvenance()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 50, autoRetries: true,
            configure: server => server.FirmwareProtocolVersion = 7);
        var result = await f.Send("REGION20 legacy", 0xA1, 1);
        await WaitCycle(f, result.MessageId, PrivateDeliveryState.Unconfirmed);
        Assert.Equal(3, f.Server.PrivateTransmissions.Count); Assert.Empty(f.Server.TemporaryScopeCommands);
        foreach (var attempt in await f.Storage.OutgoingMessages.GetAttemptsAsync(f.Node, result.MessageId, CancellationToken))
        {
            var scope = await f.Storage.OutgoingMessages.GetAttemptScopeAsync(f.Node, attempt.Id, CancellationToken);
            Assert.Equal(StoredRegionScopeState.Unknown, scope.State); Assert.False(scope.CanReplayExactly);
        }
    }
}
