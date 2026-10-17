using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Transport.Ble;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class BleIntegrationTests
{
    [Fact]
    public async Task UnknownDefaultKeepsConservativeLibraryBudgetWithoutInventingScope()
    {
        await using var f = await Fixture.Create();
        var device = await f.Connect(configure: d =>
        {
            d.Server.FirmwareProtocolVersion = 14;
            d.Server.DefaultFloodScopeResponse = [(byte)PacketType.Error, (byte)MeshCoreErrorCode.UnsupportedCommand];
        });
        var estimate = await f.Sender.GetTextBudgetAsync(f.Supervisor.Snapshot.NodeId!.Value, await BudgetRecipient(f), Token);
        Assert.Equal(StoredRegionScopeState.Unknown, estimate.Scope!.State);
        Assert.Equal(device.Transport.GetTextBudget(BleTextMessageKind.Channel, estimate.SenderName, true).MaximumTextBytes, estimate.MaximumTextBytes);
        Assert.False(estimate.Scope.CanReplayExactly);
    }

    private static byte[] BudgetFingerprint(byte value = 3) => SHA256.HashData(Enumerable.Repeat(value, 16).ToArray());
    private static async Task<OutgoingRecipient> BudgetRecipient(Fixture f) => Assert.Single(await f.Sender.GetChannelTargetsAsync(
        f.Supervisor.Snapshot.NodeId!.Value, BudgetFingerprint(), Token));

    [Fact]
    public async Task TargetBudgetsUseLibraryScopeFormulaAndExposePolicyDefaultAndOfflineFreshness()
    {
        await using var f = await Fixture.Create(); var device = await f.Connect(configure: d => d.Server.FirmwareProtocolVersion = 14);
        var owner = f.Supervisor.Snapshot; var node = owner.NodeId!.Value;
        var snapshot = await f.Storage.Directories.ApplySnapshotAsync(node, owner.SessionId!.Value, [],
            [new(0, "Scoped", BudgetFingerprint(), ChannelAccessKind.Unknown), new(1, "Unscoped", BudgetFingerprint(4), ChannelAccessKind.Unknown)], DateTimeOffset.UtcNow, Token);
        var service = new RegionService(f.Storage.Regions, TimeProvider.System, f.Gateway);
        var region = await service.CreatePublicAsync(node, "Area", "Area", Token);
        foreach (var binding in snapshot.ActiveBindings)
            await service.AssignChannelAsync(node, binding.ChannelId, binding.Slot == 0 ? FloodScopeMode.ExplicitRegion : FloodScopeMode.ForceUnscoped,
                binding.Slot == 0 ? region.Id : null, 0, Token);
        var scopedTarget = Assert.Single(await f.Sender.GetChannelTargetsAsync(node, BudgetFingerprint(), Token));
        var unscopedTarget = Assert.Single(await f.Sender.GetChannelTargetsAsync(node, BudgetFingerprint(4), Token));
        var before = device.Commands.Count;
        var a = await f.Sender.GetTextBudgetAsync(node, scopedTarget, Token);
        var b = await f.Sender.GetTextBudgetAsync(node, unscopedTarget, Token);
        Assert.Equal(device.Transport.GetTextBudget(BleTextMessageKind.Channel, a.SenderName, true).MaximumTextBytes, a.MaximumTextBytes);
        Assert.Equal(device.Transport.GetTextBudget(BleTextMessageKind.Channel, b.SenderName, false).MaximumTextBytes, b.MaximumTextBytes);
        Assert.True(a.MaximumTextBytes <= b.MaximumTextBytes); Assert.Equal(1, a.Scope!.PolicyRevision);
        Assert.Equal(StoredRegionScopeState.KnownScoped, a.Scope.State);
        Assert.Equal(StoredRegionScopeState.KnownUnscoped, b.Scope!.State);
        Assert.Equal(owner.SessionId, a.SessionId); Assert.Equal(RegionSnapshotFreshness.CurrentSession, a.Freshness);
        Assert.Equal(before, device.Commands.Count); // Presentation query sends no frames.
        var secondBinding = snapshot.ActiveBindings.Single(c => c.Slot == 1);
        await service.AssignChannelAsync(node, secondBinding.ChannelId, FloodScopeMode.InheritDefault, null, 1, Token);
        await using var lease = f.Gateway.Acquire(node, new NodeCommandTarget(), Token);
        await service.SetNodeDefaultAsync(lease.Owner, region.Definition, Token);
        var inherited = await f.Sender.GetTextBudgetAsync(node, unscopedTarget, Token);
        Assert.Equal(1, inherited.DefaultRevision); Assert.Equal(2, inherited.Scope!.PolicyRevision);
        Assert.Equal(region.Definition.Key, inherited.Scope.Key);
        device.ChangeMtu(96);
        var smaller = await f.Sender.GetTextBudgetAsync(node, scopedTarget, Token);
        Assert.True(smaller.MaximumTextBytes < a.MaximumTextBytes);
        await lease.DisposeAsync(); await f.Supervisor.DisconnectAsync(Token);
        var offline = await f.Sender.GetTextBudgetAsync(node, scopedTarget, Token);
        Assert.Null(offline.MaximumTextBytes); Assert.Null(offline.SessionId); Assert.Equal(RegionSnapshotFreshness.Offline, offline.Freshness);
    }

    [Fact]
    public async Task UnicodeAtSelectedScopeLimitIsSentExactlyAndOversizeDraftIsPreserved()
    {
        await using var f = await Fixture.Create(); var device = await f.Connect(configure: d => d.Server.FirmwareProtocolVersion = 14);
        var owner = f.Supervisor.Snapshot; var target = await BudgetRecipient(f);
        var budget = await f.Sender.GetTextBudgetAsync(owner.NodeId!.Value, target, Token);
        var count = budget.MaximumTextBytes!.Value;
        var text = new string('Я', count / 2) + (count % 2 == 0 ? "" : "a");
        Assert.Equal(count, Encoding.UTF8.GetByteCount(text));
        var draft = new DraftTarget(owner.NodeId.Value, null, ConversationKind.Channel, target.Identity.ToArray());
        await f.Drafts.LoadTextAsync(draft, Token); f.Drafts.Update(draft, text, 1);
        var sent = await f.Sender.SendChannelAsync(new(owner.NodeId.Value, owner.SessionId!.Value, owner.Generation, target, new(draft, text, 1), new()), cancellationToken: Token);
        Assert.Equal(SendAttemptState.Accepted, sent.State); Assert.Equal(text, device.Server.LastChannelText);
        var tooLong = text + "👋"; f.Drafts.Update(draft, tooLong, 2);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Sender.SendChannelAsync(new(owner.NodeId.Value, owner.SessionId.Value, owner.Generation,
            target, new(draft, tooLong, 2), new()), cancellationToken: Token));
        Assert.Equal(tooLong, await f.Drafts.LoadTextAsync(draft, Token)); Assert.Equal(1, device.Server.ChannelSendCount);
    }

    [Fact]
    public async Task BudgetShrinkingWhileAdmittedSendWaitsRefusesBeforeTextTx()
    {
        await using var f = await Fixture.Create(); var device = await f.Connect(configure: d => d.Server.FirmwareProtocolVersion = 14);
        var owner = f.Supervisor.Snapshot; var node = owner.NodeId!.Value; var target = await BudgetRecipient(f);
        var text = new string('a', (await f.Sender.GetTextBudgetAsync(node, target, Token)).MaximumTextBytes!.Value);
        var draft = new DraftTarget(node, null, ConversationKind.Channel, target.Identity.ToArray());
        await f.Drafts.LoadTextAsync(draft, Token); f.Drafts.Update(draft, text, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Server.BeforeDefaultScopeResponse = async () => { entered.TrySetResult(); await release.Task.WaitAsync(Token); };
        var service = new RegionService(f.Storage.Regions, TimeProvider.System, f.Gateway);
        await using var lease = f.Gateway.Acquire(node, new NodeCommandTarget(), Token);
        Task<RegionDefaultChangeResult>? change = null;
        var send = f.Sender.SendChannelAsync(new(node, owner.SessionId!.Value, owner.Generation, target, new(draft, text, 1), new()), async _ =>
        {
            change = service.SetNodeDefaultAsync(lease.Owner, RegionDefinition.Public("Area"), Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
        }, Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
        device.ChangeMtu(80); release.TrySetResult();
        Assert.Equal(SendAttemptState.Failed, (await send).State);
        Assert.Equal(RegionDefaultChangeOutcome.Applied, (await change!).Outcome);
        Assert.DoesNotContain(device.Commands, c => c[0] == (byte)CommandType.SendChannelTextMessage);
    }

    [Fact]
    public async Task ReconnectNameChangeInvalidatesOldBudgetAndReadinessUsesCurrentOwner()
    {
        await using var f = await Fixture.Create(); await f.Connect(); var previous = f.Supervisor.Snapshot;
        var target = await BudgetRecipient(f); var old = await f.Sender.GetTextBudgetAsync(previous.NodeId!.Value, target, Token);
        await f.Supervisor.DisconnectAsync(Token);
        await f.Connect(configure: d => d.Server.SelfName = new string('Ж', 14));
        target = await BudgetRecipient(f);
        var current = await f.Sender.GetTextBudgetAsync(previous.NodeId.Value, target, Token);
        Assert.NotEqual(old.SessionId, current.SessionId); Assert.True(current.MaximumTextBytes < old.MaximumTextBytes);
        var reader = new SendReadinessReader(f.Storage.Directories, f.Storage.ConversationDirectory, f.Sender);
        var recipient = new SendRecipient(ConversationKind.Channel, target.Identity, target.Slot);
        Assert.Equal(SendReadiness.NotOnline, (await reader.ReadBudgetAsync(previous, previous.NodeId, recipient, Token)).Readiness);
        var ready = await reader.ReadBudgetAsync(f.Supervisor.Snapshot, previous.NodeId, recipient, Token);
        Assert.Equal(SendReadiness.Ready, ready.Readiness); Assert.Equal(current.SessionId, ready.Budget!.SessionId);
    }
}
