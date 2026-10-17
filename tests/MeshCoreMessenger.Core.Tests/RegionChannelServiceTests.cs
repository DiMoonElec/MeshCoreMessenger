using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    [Fact]
    public async Task LegacyDefaultEmptyUsesInheritedPathWithoutSilentlyDowngradingForceUnscoped()
    {
        await using var f = await Fixture.CreateAsync(); f.Clients.Configure = client => client.ProtocolVersion = 10;
        await f.Connect(); var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        var request = await Request(f, drafts); var sender = Sender(f, drafts);
        var first = await sender.SendChannelAsync(request, cancellationToken: CancellationToken);
        Assert.Equal(SendAttemptState.Accepted, first.State);
        Assert.Equal(FloodScopeMode.InheritDefault, f.Clients.Current!.SentScope!.Mode);
        var binding = (await f.Storage.Directories.GetActiveChannelBindingAsync(f.NodeId, 7, CancellationToken))!;
        await f.Storage.Regions.SetChannelPreferenceAsync(new(f.NodeId, binding.ChannelId, FloodScopeMode.ForceUnscoped, null, 0), CancellationToken);
        var result = await sender.SendChannelAsNewAsync(new(f.NodeId, request.SessionId, request.Generation, first.MessageId, 1, request.Recipient), CancellationToken);
        Assert.Equal(SendAttemptState.Failed, result.State); Assert.Equal(1, f.Clients.Current.Tx);
    }

    [Fact]
    public async Task ChannelScopeCaptureSurvivesPreferenceChangeAndExactRepeatUsesOriginal()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System); var sender = Sender(f, drafts);
        var request = await Request(f, drafts); var binding = (await f.Storage.Directories.GetActiveChannelBindingAsync(f.NodeId, 7, CancellationToken))!;
        var regions = new RegionService(f.Storage.Regions, TimeProvider.System, f.Gateway);
        var a = await regions.CreatePublicAsync(f.NodeId, "A", "A", CancellationToken);
        var b = await regions.CreatePublicAsync(f.NodeId, "B", "B", CancellationToken);
        var policy = await regions.AssignChannelAsync(f.NodeId, binding.ChannelId, FloodScopeMode.ExplicitRegion, a.Id, 0, CancellationToken);
        var sent = await sender.SendChannelAsync(request, async _ =>
        {
            await regions.AssignChannelAsync(f.NodeId, binding.ChannelId, FloodScopeMode.ExplicitRegion, b.Id, policy.Revision, CancellationToken);
        }, CancellationToken);
        Assert.Equal(a.Definition.Key, f.Clients.Current!.SentScope!.Key);
        var original = Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, sent.MessageId, CancellationToken));
        var capture = await f.Storage.OutgoingMessages.GetAttemptScopeAsync(f.NodeId, original.Id, CancellationToken);
        Assert.Equal("A", capture.Name); Assert.Equal(policy.Revision, capture.PolicyRevision);
        var repeat = new ChannelRepeatRequest(f.NodeId, request.SessionId, request.Generation, sent.MessageId, 1, request.Recipient);
        await sender.RepeatChannelAsync(repeat, CancellationToken);
        Assert.Equal(a.Definition.Key, f.Clients.Current.SentScope!.Key);
        var copy = await sender.SendChannelAsNewAsync(repeat, CancellationToken);
        Assert.NotEqual(sent.MessageId, copy.MessageId);
        Assert.Equal(b.Definition.Key, f.Clients.Current.SentScope!.Key);
    }

    [Fact]
    public async Task InheritedDefaultIsPinnedWhenDefaultChangesAfterPreparation()
    {
        await using var f = await Fixture.CreateAsync();
        var initial = RegionDefinition.Public("A"); var changed = RegionDefinition.Public("B");
        f.Clients.Configure = client => client.DefaultScope = NodeFloodScope.FromPreferences(initial.Name, initial.Key);
        await f.Connect(); var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        var request = await Request(f, drafts); var regions = new RegionService(f.Storage.Regions, TimeProvider.System, f.Gateway);
        var result = await Sender(f, drafts).SendChannelAsync(request, async _ =>
        {
            await using var owner = f.Acquire();
            Assert.Equal(RegionDefaultChangeOutcome.Applied, (await regions.SetNodeDefaultAsync(owner.Owner, changed, CancellationToken)).Outcome);
        }, CancellationToken);
        Assert.Equal(SendAttemptState.Accepted, result.State);
        Assert.Equal(initial.Key, f.Clients.Current!.SentScope!.Key);
        Assert.Equal(changed.Key, f.Gateway.GetDefaultRegion(f.NodeId)!.Value.Key);
    }

    [Fact]
    public async Task DuplicateSlotsUseOneChannelPreferenceAndKeepBindingGeneration()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System); var request = await Request(f, drafts);
        var snapshot = await f.Storage.Directories.ApplySnapshotAsync(f.NodeId, request.SessionId, [],
            [new(7, "First", request.Recipient.Identity.ToArray(), ChannelAccessKind.Unknown), new(2, "Second", request.Recipient.Identity.ToArray(), ChannelAccessKind.Unknown)], DateTimeOffset.UtcNow, CancellationToken);
        var binding = snapshot.ActiveBindings.Single(b => b.Slot == 2); var regionService = new RegionService(f.Storage.Regions, TimeProvider.System);
        var region = await regionService.CreatePublicAsync(f.NodeId, "A", "A", CancellationToken);
        await regionService.AssignChannelAsync(f.NodeId, binding.ChannelId, FloodScopeMode.ExplicitRegion, region.Id, 0, CancellationToken);
        var recipient = new OutgoingRecipient(ConversationKind.Channel, request.Recipient.Identity, binding.Id, binding.Slot, binding.Generation);
        Assert.Equal(SendAttemptState.Accepted, (await Sender(f, drafts).SendChannelAsync(request with { Recipient = recipient }, cancellationToken: CancellationToken)).State);
        Assert.Equal(region.Definition.Key, f.Clients.Current!.SentScope!.Key);
        Assert.Equal(binding.Generation, (await f.Storage.Directories.GetActiveChannelBindingAsync(f.NodeId, 2, CancellationToken))!.Generation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopeFailureBeforeSendIsFailedButAcceptedCleanupFailureIsNotRetried(bool accepted)
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect(); var client = f.Clients.Current!;
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        if (accepted) client.CleanupError = new IOException("RESET outcome unknown");
        else client.ChannelSendAction = () => throw new MeshCoreTimeoutException("SET", TimeSpan.FromMilliseconds(1)) { TextSendWasInvoked = false };
        var result = await Sender(f, drafts).SendChannelAsync(await Request(f, drafts), cancellationToken: CancellationToken);
        Assert.Equal(accepted ? SendAttemptState.Accepted : SendAttemptState.Failed, result.State);
        Assert.Equal(accepted, result.ScopeCleanupError is not null); Assert.Equal(1, client.Tx);
        var attempt = Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, result.MessageId, CancellationToken));
        Assert.Equal(accepted ? "ScopeCleanupFailed" : nameof(MeshCoreTimeoutException), attempt.ErrorCode);
    }

    [Fact]
    public async Task LegacyRepeatRefusesUnknownScopeButAsNewUsesCurrentPolicy()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        var request = await Request(f, drafts);
        var legacy = new MessageService(f.Gateway, f.Storage.OutgoingMessages, f.Storage.Directories, f.Storage.ConversationDirectory,
            drafts, new PassthroughOutgoingTextProcessor(), TimeProvider.System, f.Storage.Drafts, f.Operations);
        var first = await legacy.SendChannelAsync(request, cancellationToken: CancellationToken);
        var repeat = new ChannelRepeatRequest(f.NodeId, request.SessionId, request.Generation, first.MessageId, 1, request.Recipient);
        var sender = Sender(f, drafts);
        await Assert.ThrowsAsync<RegionReplayUnavailableException>(() => sender.RepeatChannelAsync(repeat, CancellationToken));
        Assert.Equal(1, f.Clients.Current!.Tx);
        Assert.Equal(SendAttemptState.Accepted, (await sender.SendChannelAsNewAsync(repeat, CancellationToken)).State);
    }
}
