using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    private static byte[] RepeaterKey(byte suffix) { var key = Enumerable.Repeat((byte)0xA1, 32).ToArray(); key[^1] = suffix; return key; }
    private static Contact Repeater(byte suffix, byte route = 0, byte[]? path = null) => new(RepeaterKey(suffix), AdvertisementType.Repeater,
        0, route, path ?? new byte[64], "Repeater", 1, 1, 2, 3);
    private static DiscoveredNeighbor Neighbor(byte suffix, bool full = true, byte path = 0) =>
        new(AdvertisementType.Repeater, 1, full ? RepeaterKey(suffix) : RepeaterKey(suffix)[..8], 1, 2, -50, path);

    [Fact]
    public async Task PreflightMergesOnlyFullKeysRefreshesRoutesAndReportsEveryMutationWithoutExecutingIt()
    {
        await using var f = await Fixture.CreateAsync(); var packed = new byte[64]; new byte[] { 1, 2, 3, 4 }.CopyTo(packed, 0);
        var contacts = new[] { Repeater(1), Repeater(2, 0x42, packed), Repeater(3, 255), Repeater(4, 255) };
        f.Clients.Configure = c => { c.PreflightContacts = contacts; c.ContactRead = (key, _) => Task.FromResult(contacts.Single(x => x.PublicKey.Span.SequenceEqual(key.Span))); };
        await f.Connect(); await using var owner = f.Acquire(); var before = f.Clients.Current!.Tx;
        var service = new RegionDiscoveryPreflight(f.Gateway, TimeProvider.System);
        var result = await service.PrepareAsync(owner.Owner, [Neighbor(3), Neighbor(5), Neighbor(6, false)], cancellationToken: CancellationToken);
        Assert.Equal(5, result.Plans.Count); Assert.Equal(1, result.UnresolvedPrefixCount); Assert.Equal(0, result.OmittedTargetCount);
        RegionDiscoveryPlan Plan(byte key) => result.Plans.Single(p => p.GetPublicKey()[^1] == key);
        Assert.Equal(RegionDiscoveryPreparation.ReadyDirect, Plan(1).Preparation);
        Assert.Equal(RegionReplyPathEvidence.CurrentZeroHopPath, Plan(1).ReplyPathEvidence);
        Assert.Equal(RegionReplyPathEvidence.AssumedReverse, Plan(2).ReplyPathEvidence);
        Assert.Equal(new byte[] { 3, 4, 1, 2 }, Plan(2).ReplyPath!.ToArray());
        Assert.Equal(RegionDiscoveryPreparation.RequiresTemporaryZeroHop, Plan(3).Preparation); Assert.True(Plan(3).RequiresConditionalRestore);
        Assert.Equal(RegionDiscoveryPreparation.NoUsableReplyPath, Plan(4).Preparation);
        Assert.Equal(RegionDiscoveryPreparation.RequiresContactCreation, Plan(5).Preparation); Assert.True(Plan(5).MayCreateContact);
        packed[0] = 9;
        Assert.False(Plan(2).MatchesContact(contacts[1])); Assert.Equal(1, Plan(2).GetContactSnapshot()!.OutPath.Span[0]);
        Assert.All(result.Plans, p => Assert.True(p.MatchesOwner(owner.Owner.NodeId, owner.Owner.SessionId, owner.Owner.Generation)));
        Assert.Equal(before, f.Clients.Current.Tx); Assert.Empty(f.Clients.Current.Mutations);
    }

    [Fact]
    public async Task ConfirmedAsymmetricReplyPathIsNotReversedAndPlanDetectsChangedAdvertAndRoute()
    {
        await using var f = await Fixture.CreateAsync(); var contact = Repeater(1, 1); var now = contact;
        f.Clients.Configure = c => { c.PreflightContacts = [contact]; c.ContactRead = (_, _) => Task.FromResult(now); };
        await f.Connect(); await using var owner = f.Acquire(); var service = new RegionDiscoveryPreflight(f.Gateway, TimeProvider.System);
        var confirmed = new ReplyPath(1, new byte[] { 0xE7 });
        var plan = Assert.Single((await service.PrepareAsync(owner.Owner, [], [new(RepeaterKey(1), confirmed)], cancellationToken: CancellationToken)).Plans);
        Assert.Equal(RegionReplyPathEvidence.Confirmed, plan.ReplyPathEvidence); Assert.Equal(new byte[] { 0xE7 }, plan.ReplyPath!.ToArray());
        Assert.True(plan.MatchesContact(contact));
        now = contact with { LastAdvertTimestamp = 2 }; Assert.False(plan.MatchesContact(now));
        Assert.True(plan.MatchesContact(plan.GetContactSnapshot()!));
        Assert.False(plan.MatchesOwner(owner.Owner.NodeId, owner.Owner.SessionId, owner.Owner.Generation + 1));
        var next = Assert.Single((await service.PrepareAsync(owner.Owner, [], cancellationToken: CancellationToken)).Plans);
        Assert.NotEqual(plan.ContactRevision, next.ContactRevision);
    }

    [Fact]
    public async Task EmptyContactsAndPrefixOnlyOrNonDirectNeighborsCannotInventAContactTarget()
    {
        await using var f = await Fixture.CreateAsync(); f.Clients.Configure = c => c.PreflightContacts = [];
        await f.Connect(); await using var owner = f.Acquire(); var service = new RegionDiscoveryPreflight(f.Gateway, TimeProvider.System);
        var result = await service.PrepareAsync(owner.Owner, [Neighbor(1, false), Neighbor(2, path: 1)], cancellationToken: CancellationToken);
        Assert.Equal(1, result.UnresolvedPrefixCount); Assert.Equal(RegionDiscoveryPreparation.NoUsableReplyPath, Assert.Single(result.Plans).Preparation);
        Assert.Equal(0, f.Clients.Current!.Tx); Assert.Empty(f.Clients.Current.Mutations);
        Assert.Empty((await service.PrepareAsync(owner.Owner, [], cancellationToken: CancellationToken)).Plans);
    }

    [Fact]
    public async Task PreflightRejectsStaleOwnerWrongFullKeyAndLateCanceledReadback()
    {
        await using var f = await Fixture.CreateAsync(); f.Clients.Configure = c => c.PreflightContacts = [Repeater(1)];
        await f.Connect(); await using var owner = f.Acquire(); var service = new RegionDiscoveryPreflight(f.Gateway, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(owner.Owner with { Generation = owner.Owner.Generation + 1 }, [], cancellationToken: CancellationToken));
        f.Clients.Current!.ContactRead = (_, _) => Task.FromResult(Repeater(2));
        await Assert.ThrowsAsync<MeshCoreProtocolException>(() => service.PrepareAsync(owner.Owner, [], cancellationToken: CancellationToken));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        f.Clients.Current.ContactRead = (_, _) => { cancel.Cancel(); return Task.FromResult(Repeater(1)); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync(owner.Owner, [], cancellationToken: cancel.Token));
        Assert.Equal(0, f.Clients.Current.Tx);
    }

    [Fact]
    public async Task PreflightUsesFreshRouteAndBoundsTargetsWithoutHidingOmissions()
    {
        await using var f = await Fixture.CreateAsync();
        f.Clients.Configure = c => { c.PreflightContacts = [Repeater(1), Repeater(2)]; c.ContactRead = (key, _) => Task.FromResult(Repeater(key.Span[^1], 255)); };
        await f.Connect(); await using var owner = f.Acquire();
        var result = await new RegionDiscoveryPreflight(f.Gateway, TimeProvider.System).PrepareAsync(owner.Owner, [Neighbor(1)], maximumTargets: 1, cancellationToken: CancellationToken);
        Assert.Equal(1, result.OmittedTargetCount);
        Assert.Equal(RegionDiscoveryPreparation.RequiresTemporaryZeroHop, Assert.Single(result.Plans).Preparation);
        Assert.Equal(0, f.Clients.Current!.Tx);
    }
}
