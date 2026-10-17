using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    [Fact]
    public async Task FreshRepeaterDiscoveryCanReuseFirmwareAnonymousContactWithoutRepeaterType()
    {
        await using var f = await DiscoveryFixture.Create(1);
        var contact = f.Contacts.Values.Single() with { AdvertisementType = AdvertisementType.None };
        f.Contacts[contact.PublicKeyHex] = contact; f.Client.PreflightContacts = [contact];
        f.Client.NeighborScan = (_, _) => Task.FromResult(new NeighborDiscoveryResult(1, [Neighbor(1)], false));
        var result = await f.Service.DiscoverAsync(f.Owner, RegionDiscoveryOptions.DirectNeighbors(), cancellationToken: CancellationToken);
        Assert.NotNull(Assert.Single(result.Names)); Assert.Equal(1, f.Client.RegionRequestCount);
        Assert.Equal(RegionDiscoveryTargetStatus.SuccessPossiblyPartial, Assert.Single(result.Outcomes).Status);
        Assert.Empty(f.Client.Mutations);
    }

    [Fact]
    public async Task SimpleDiscoveryWireRetriesTimeoutWithZeroHopAndRestoresMultiHopContact()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(true, configure: server =>
        {
            server.FirmwareProtocolVersion = 14;
            server.AddRepeater(0xC1, 1, "ru-ros"); server.AddRepeater(0xC2, 0, "not-a-neighbor");
            server.NeighborRepeaters.Add(0xC1);
        });
        f.Server.BeforeRegionResponse = peer =>
        {
            if (f.Server.RegionRequests.Count == 1) f.Server.SilentRegionRepeaters.Add(peer);
            else f.Server.SilentRegionRepeaters.Remove(peer);
            return Task.CompletedTask;
        };
        await using var owner = f.Gateway.Acquire(f.Node, new NodeCommandTarget(), CancellationToken);
        var options = RegionDiscoveryOptions.DirectNeighbors() with
        {
            Neighbors = new() { CollectionWindow = TimeSpan.FromMilliseconds(50) },
            RequestTimeout = TimeSpan.FromMilliseconds(100), RetryDelay = TimeSpan.Zero
        };
        var result = await WireDiscovery(f).DiscoverAsync(owner.Owner, options, cancellationToken: CancellationToken);
        Assert.Null(result.Error); Assert.Equal("ru-ros", Assert.Single(result.Names).ExactName);
        Assert.Equal(2, f.Server.RegionRequests.Count);
        Assert.All(f.Server.RegionRequests, command =>
        {
            Assert.Equal((byte)0xC1, command[1]); Assert.Equal((byte)1, command[33]);
            Assert.Equal((byte)0, command[34]); Assert.Equal(35, command.Length);
        });
        Assert.Equal((byte)1, f.Server.ContactRoutes[0xC1]); Assert.Equal(2, f.Server.ContactUpdates.Count);
        Assert.Equal(RegionRouteRestorationOutcome.Restored, Assert.Single(result.Outcomes).Restoration);
        Assert.Single(await f.Storage.Regions.GetDiscoveryOutcomesAsync(f.Node, CancellationToken));
    }

    [Fact]
    public async Task DirectDiscoverySelectsFourStrongestObservedFullZeroHopKeysBeforePreflight()
    {
        await using var f = await DiscoveryFixture.Create(8);
        DiscoveredNeighbor Seen(byte key, sbyte rssi, byte path = 0, bool full = true, AdvertisementType type = AdvertisementType.Repeater) =>
            new(type, 1, full ? RepeaterKey(key) : RepeaterKey(key)[..8], 0, 2, rssi, path);
        f.Client.NeighborScan = (options, _) =>
        {
            Assert.Equal(TimeSpan.FromSeconds(30), options.CollectionWindow);
            return Task.FromResult(new NeighborDiscoveryResult(1,
                [Seen(1, -90), Seen(5, -30), Seen(4, -40), Seen(3, -50), Seen(2, -60), Seen(2, -70),
                 Seen(6, -10, path: 1), Seen(7, -5, full: false), Seen(8, -1, type: AdvertisementType.Chat)], false));
        };
        var reads = new List<byte>();
        f.Client.ContactRead = (key, _) => { reads.Add(key.Span[^1]); return Task.FromResult(f.Contacts[Convert.ToHexString(key.Span)]); };
        var requests = new List<byte>();
        f.Client.RegionRequest = (key, reply, _) =>
        {
            Assert.Equal(0, reply.Descriptor); Assert.Empty(reply.ToArray());
            requests.Add(key.Span[^1]); return Task.FromResult(new RegionNamesResponse(1, 2, ["ru-ros"]));
        };
        var progress = new List<RegionDiscoveryProgress>();
        var result = await f.Service.DiscoverAsync(f.Owner, RegionDiscoveryOptions.DirectNeighbors(), new DiscoveryProgress(progress.Add), CancellationToken);
        Assert.Equal(new byte[] { 5, 4, 3, 2 }, requests);
        Assert.All(reads, key => Assert.Contains(key, requests));
        Assert.Equal(1, result.OmittedTargets); Assert.Single(result.Names);
        Assert.Contains(progress, p => p.Names?.Count == 1 && p.Completed == 1 && p.Owner?.SessionId == f.Owner.SessionId);
        Assert.Empty(await f.App.Storage.Regions.GetRegionsAsync(f.Owner.NodeId, cancellationToken: CancellationToken));
    }

    [Fact]
    public async Task EmptyDirectDiscoveryDoesNotReadKnownContactsOrIssueRegionRequests()
    {
        await using var f = await DiscoveryFixture.Create();
        f.Client.NeighborScan = (_, _) => Task.FromResult(new NeighborDiscoveryResult(1, [], false));
        f.Client.ContactRead = (_, _) => throw new InvalidOperationException("No preflight expected.");
        var result = await f.Service.DiscoverAsync(f.Owner, RegionDiscoveryOptions.DirectNeighbors(), cancellationToken: CancellationToken);
        Assert.Empty(result.Outcomes); Assert.Empty(result.Plans); Assert.Null(result.Error); Assert.Equal(0, f.Client.RegionRequestCount);
    }

    [Theory]
    [InlineData((byte)255)]
    [InlineData((byte)1)]
    public async Task SimpleRequestsRetryRemoteTimeoutWithinOneZeroHopPreparationAndRestore(byte originalRoute)
    {
        await using var f = await RegionRouteFixture.Create(route: originalRoute);
        await using var owner = f.App.Acquire();
        var plan = Assert.Single((await new RegionDiscoveryPreflight(f.App.Gateway, TimeProvider.System).PrepareAsync(owner.Owner,
            [Neighbor(1)], cancellationToken: CancellationToken, onlyDiscoveredNeighbors: true)).Plans);
        Assert.True(plan.RequiresConditionalRestore); Assert.Equal(0, plan.ReplyPath!.Descriptor);
        var calls = 0;
        f.State.Request = _ =>
        {
            Assert.Equal(0, f.State.Current!.OutPathLength);
            if (++calls < 3) throw new MeshCoreTimeoutException("RemoteBinaryResponse", TimeSpan.FromSeconds(15));
            return Task.FromResult(new RegionNamesResponse(1, 2, []));
        };
        var result = await f.Workflow.RequestWithRetryAsync(plan, new(true, true), TimeSpan.FromSeconds(15), 3, TimeSpan.Zero, CancellationToken);
        Assert.Equal(3, calls); Assert.NotNull(result.Response); Assert.Empty(result.Response.Names);
        Assert.Equal(2, f.State.Writes); Assert.Equal(originalRoute, f.State.Current!.OutPathLength);
        Assert.Equal(RegionRouteRestorationOutcome.Restored, result.Restoration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImmediateTimeoutAndValidEmptyResponseNeverRetry(bool empty)
    {
        await using var f = await RegionRouteFixture.Create(route: 0);
        f.State.Request = _ => empty ? Task.FromResult(new RegionNamesResponse(1, 2, [])) :
            throw new MeshCoreTimeoutException("RequestRegionsAsync", TimeSpan.FromSeconds(5));
        var result = await f.Workflow.RequestWithRetryAsync(f.Plan, new(true, true), TimeSpan.FromSeconds(15), 3, TimeSpan.Zero, CancellationToken);
        Assert.Equal(1, f.Client.RegionRequestCount); Assert.Equal(empty, result.Response is not null);
    }

    [Fact]
    public async Task CancellationDuringRetryDelayRestoresRouteWithoutNextTransmission()
    {
        await using var f = await RegionRouteFixture.Create(); using var stop = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        var entered = NewGate();
        f.State.Request = _ => { entered.TrySetResult(); throw new MeshCoreTimeoutException("RemoteBinaryResponse", TimeSpan.FromSeconds(15)); };
        var task = f.Workflow.RequestWithRetryAsync(f.Plan, new(true, true), TimeSpan.FromSeconds(15), 3, TimeSpan.FromSeconds(5), stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken); stop.Cancel();
        var result = await task;
        Assert.IsAssignableFrom<OperationCanceledException>(result.Error); Assert.Equal(1, f.Client.RegionRequestCount);
        Assert.Equal(RegionRouteRestorationOutcome.Restored, result.Restoration); Assert.Equal(255, f.State.Current!.OutPathLength);
    }

    [Fact]
    public async Task ChangedContactStopsRetryAndPreservesNewRoute()
    {
        await using var f = await RegionRouteFixture.Create();
        f.State.Request = _ =>
        {
            f.State.Current = f.State.Current! with { OutPathLength = 1, LastModified = 99 };
            throw new MeshCoreTimeoutException("RemoteBinaryResponse", TimeSpan.FromSeconds(15));
        };
        var result = await f.Workflow.RequestWithRetryAsync(f.Plan, new(true, true), TimeSpan.FromSeconds(15), 3, TimeSpan.Zero, CancellationToken);
        Assert.Equal(1, f.Client.RegionRequestCount); Assert.Equal(1, f.State.Writes);
        Assert.Equal(RegionRouteRestorationOutcome.Conflict, result.Restoration); Assert.Equal(1, f.State.Current!.OutPathLength);
    }
}
