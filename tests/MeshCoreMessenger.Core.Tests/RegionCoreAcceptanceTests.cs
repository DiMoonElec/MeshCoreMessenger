using System.Security.Cryptography;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    private static RegionDiscoveryService WireDiscovery(TcpPrivateFixture f) => new(f.Gateway,
        new RegionDiscoveryPreflight(f.Gateway, TimeProvider.System),
        new RegionDiscoveryRouteWorkflow(f.Gateway, f.Storage.Directories, f.Operations, TimeProvider.System),
        f.Storage.Regions, TimeProvider.System);

    private static RegionDiscoveryOptions WireDiscoveryOptions() => new()
    {
        Neighbors = new() { CollectionWindow = TimeSpan.FromMilliseconds(50) },
        RequestTimeout = TimeSpan.FromMilliseconds(150), TotalTimeout = TimeSpan.FromSeconds(10), Consent = new(true, true),
    };

    [Fact]
    public async Task RegionWireCatalogueDefaultChannelAndDiscoveryKeepContinuousIngestAndHistory()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(true, configure: server =>
        {
            server.FirmwareProtocolVersion = 14;
            server.AddRepeater(0xC1, 0xFF, "Area,$secret,*"); server.AddRepeater(0xC2, 0, "");
            server.NeighborRepeaters.UnionWith([0xC1, 0xC3]); server.RepeaterRegionCsv[0xC3] = "Area";
        });
        await using var lease = f.Gateway.Acquire(f.Node, new NodeCommandTarget(), CancellationToken);
        var regions = new RegionService(f.Storage.Regions, TimeProvider.System, f.Gateway);
        var a = await regions.CreatePublicAsync(f.Node, "Default A", "A", CancellationToken);
        var b = await regions.CreatePublicAsync(f.Node, "Channel B", "B", CancellationToken);
        Assert.Equal(RegionDefaultChangeOutcome.Applied, (await regions.SetNodeDefaultAsync(lease.Owner, a.Definition, CancellationToken)).Outcome);
        var fingerprint = SHA256.HashData(Enumerable.Repeat((byte)3, 16).ToArray());
        var target = Assert.Single(await f.Sender.GetChannelTargetsAsync(f.Node, fingerprint, CancellationToken));
        var binding = (await f.Storage.Directories.GetActiveChannelBindingAsync(f.Node, 0, CancellationToken))!;
        await regions.AssignChannelAsync(f.Node, binding.ChannelId, FloodScopeMode.ExplicitRegion, b.Id, 0, CancellationToken);
        var draft = new DraftTarget(f.Node, null, ConversationKind.Channel, target.Identity.ToArray());
        await f.Drafts.LoadTextAsync(draft, CancellationToken); f.Drafts.Update(draft, "Channel in B", 1);
        var sent = await f.Sender.SendChannelAsync(new(f.Node, lease.Owner.SessionId, lease.Owner.Generation, target,
            new(draft, "Channel in B", 1), new()), cancellationToken: CancellationToken);
        Assert.Equal(SendAttemptState.Accepted, sent.State); Assert.Equal(b.Definition.Key.ToArray(), Assert.Single(f.Server.ChannelScopeKeys));
        var channelAttempt = Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.Node, sent.MessageId, CancellationToken));
        Assert.Equal(b.Definition.Key, (await f.Storage.OutgoingMessages.GetAttemptScopeAsync(f.Node, channelAttempt.Id, CancellationToken)).Key);
        var privateSent = await f.Send("Private in A", 0xA1, 1); await f.WaitState(privateSent.MessageId, SendAttemptState.Delivered);
        Assert.Equal(a.Definition.Key.ToArray(), Assert.Single(f.Server.PrivateTransmissions).ScopeKey.ToArray());
        f.Server.BeforeRegionResponse = async peer =>
        {
            if (peer == 0xC1) await f.Server.QueueIncomingMessageAsync(FakeCompanionServer.BuildMessage(PacketType.ContactMessageReceivedV3, "During discovery"));
        };
        var result = await WireDiscovery(f).DiscoverAsync(lease.Owner, WireDiscoveryOptions(), cancellationToken: CancellationToken);
        Assert.Null(result.Error); Assert.Equal(3, result.Outcomes.Count); Assert.All(result.Outcomes, o => Assert.True(o.Persisted));
        Assert.Equal(2, result.Names.Single(n => n.ExactName == "Area").SourcePublicKeys.Count);
        Assert.Equal(DiscoveredRegionNameKind.PrivateKeyRequired, result.Names.Single(n => n.ExactName == "$secret").Kind);
        Assert.Contains(result.Outcomes, o => o.Status == RegionDiscoveryTargetStatus.SuccessEmpty);
        Assert.Contains(result.Outcomes, o => o.ContactCreation == RegionContactCreationOutcome.Observed);
        Assert.Equal((byte)0xFF, f.Server.ContactRoutes[0xC1]); Assert.True(f.Server.RepeaterContacts.ContainsKey(0xC3));
        var updates = f.Server.ContactUpdates.ToArray(); Assert.Equal(2, updates.Length);
        Assert.Equal((byte)0, updates[0][35]); Assert.Equal((byte)0xFF, updates[1][35]);
        Assert.Equal(updates[0].AsSpan(1, 34).ToArray(), updates[1].AsSpan(1, 34).ToArray());
        Assert.Equal(3, f.Server.RegionRequests.Count);
        Assert.Equal(3, (await f.Storage.Regions.GetDiscoveryOutcomesAsync(f.Node, CancellationToken)).Count);
        Assert.Equal(2, (await regions.GetRegionsAsync(f.Node, cancellationToken: CancellationToken)).Count); // Discovery never imports keys.
        await Until(() => HasWireMessage(f, "During discovery"));
        await f.Supervisor.ShutdownAsync(CancellationToken); await f.Server.Completion.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken);
        Assert.Equal(3, f.Server.RegionRequests.Count); Assert.Single(f.Server.PrivateTransmissions); Assert.Equal(1, f.Server.ChannelSendCount);
        Assert.True(HasWireMessage(f, "During discovery"));
        Assert.Null(f.Gateway.GetDefaultRegion(f.Node)); // Closed session no longer supplies current observations.
        Assert.Equal("A", (await f.Storage.Regions.GetNodeSnapshotAsync(f.Node, CancellationToken))!.Value.Name);
    }

    private static bool HasWireMessage(TcpPrivateFixture f, string text)
    {
        using var c = SqliteDatabase.CreateConnection(f.Paths.DatabasePath, SqliteOpenMode.ReadOnly); c.Open();
        using var command = c.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM Messages WHERE Text=$text;";
        command.Parameters.AddWithValue("$text", text); return (long)command.ExecuteScalar()! == 1;
    }

    [Fact]
    public async Task RegionWireExplicitUnscopedInheritAndExactRepeatKeepIndependentPolicies()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(true, configure: server => server.FirmwareProtocolVersion = 14);
        await using var lease = f.Gateway.Acquire(f.Node, new NodeCommandTarget(), CancellationToken);
        var service = new RegionService(f.Storage.Regions, TimeProvider.System, f.Gateway);
        var a = await service.CreatePublicAsync(f.Node, "A", "A", CancellationToken);
        var b = await service.CreatePublicAsync(f.Node, "B", "B", CancellationToken);
        await service.SetNodeDefaultAsync(lease.Owner, a.Definition, CancellationToken);
        var binding = (await f.Storage.Directories.GetActiveChannelBindingAsync(f.Node, 0, CancellationToken))!;
        var target = Assert.Single(await f.Sender.GetChannelTargetsAsync(f.Node, SHA256.HashData(Enumerable.Repeat((byte)3, 16).ToArray()), CancellationToken));
        var draft = new DraftTarget(f.Node, null, ConversationKind.Channel, target.Identity.ToArray());
        await f.Drafts.LoadTextAsync(draft, CancellationToken);
        long revision = 0;
        async Task<ChannelSendOutcome> Send()
        {
            var text = "scope sequence " + ++revision; f.Drafts.Update(draft, text, revision);
            return await f.Sender.SendChannelAsync(new(f.Node, lease.Owner.SessionId, lease.Owner.Generation, target, new(draft, text, revision), new()), cancellationToken: CancellationToken);
        }
        await service.AssignChannelAsync(f.Node, binding.ChannelId, FloodScopeMode.ExplicitRegion, b.Id, 0, CancellationToken);
        var first = await Send();
        var privateSent = await f.Send("Private between channels", 0xA1, 1); await f.WaitState(privateSent.MessageId, SendAttemptState.Delivered);
        await service.AssignChannelAsync(f.Node, binding.ChannelId, FloodScopeMode.ForceUnscoped, null, 1, CancellationToken); await Send();
        await service.AssignChannelAsync(f.Node, binding.ChannelId, FloodScopeMode.InheritDefault, null, 2, CancellationToken); await Send();
        var repeat = new ChannelRepeatRequest(f.Node, lease.Owner.SessionId, lease.Owner.Generation, first.MessageId, 1, target);
        await f.Sender.RepeatChannelAsync(repeat, CancellationToken);
        await f.Sender.SendChannelAsNewAsync(repeat, CancellationToken);
        var keys = f.Server.ChannelScopeKeys.ToArray(); Assert.Equal(5, keys.Length);
        Assert.Equal(b.Definition.Key.ToArray(), keys[0]); Assert.Empty(keys[1]); Assert.Equal(a.Definition.Key.ToArray(), keys[2]);
        Assert.Equal(b.Definition.Key.ToArray(), keys[3]); Assert.Equal(a.Definition.Key.ToArray(), keys[4]);
        Assert.Equal(a.Definition.Key.ToArray(), Assert.Single(f.Server.PrivateTransmissions).ScopeKey.ToArray());
        var attempts = await f.Storage.OutgoingMessages.GetAttemptsAsync(f.Node, first.MessageId, CancellationToken);
        Assert.Equal(2, attempts.Count);
        foreach (var attempt in attempts) Assert.Equal(b.Definition.Key, (await f.Storage.OutgoingMessages.GetAttemptScopeAsync(f.Node, attempt.Id, CancellationToken)).Key);
        var current = Assert.Single(await f.Sender.GetChannelTargetsAsync(f.Node, target.Identity, CancellationToken));
        Assert.Equal(target.ChannelBindingId, current.ChannelBindingId); Assert.Equal(target.BindingGeneration, current.BindingGeneration);
    }

    [Theory]
    [InlineData("conflict", RegionDiscoveryTargetStatus.SuccessWithRouteWarning)]
    [InlineData("malformed", RegionDiscoveryTargetStatus.Malformed)]
    [InlineData("timeout", RegionDiscoveryTargetStatus.Timeout)]
    public async Task RegionWireFailuresPreserveOtherSourcesAndNeverReplay(string mode, RegionDiscoveryTargetStatus expected)
    {
        await using var f = await TcpPrivateFixture.CreateAsync(true, configure: server =>
        {
            server.FirmwareProtocolVersion = 14; server.AddRepeater(0xC1, 0xFF, mode == "malformed" ? "A,,B" : "Area");
            server.AddRepeater(0xC2, 0, "Other"); server.NeighborRepeaters.Add(0xC1);
            if (mode == "timeout") server.SilentRegionRepeaters.Add(0xC1);
        });
        if (mode == "conflict") f.Server.BeforeRegionResponse = peer => peer == 0xC1 ? f.Server.SendPathUpdatedAsync(peer, 1) : Task.CompletedTask;
        await using var lease = f.Gateway.Acquire(f.Node, new NodeCommandTarget(), CancellationToken);
        var result = await WireDiscovery(f).DiscoverAsync(lease.Owner, WireDiscoveryOptions(), cancellationToken: CancellationToken);
        Assert.Null(result.Error); Assert.Equal(2, result.Outcomes.Count); Assert.Equal(expected, result.Outcomes[0].Status);
        Assert.Contains(result.Names, n => n.ExactName == "Other"); Assert.All(result.Outcomes, o => Assert.True(o.Persisted));
        Assert.Equal(mode == "conflict" ? (byte)1 : (byte)0xFF, f.Server.ContactRoutes[0xC1]);
        Assert.Equal(mode == "conflict" ? 1 : 2, f.Server.ContactUpdates.Count);
        Assert.Equal(2, f.Server.RegionRequests.Count); Assert.Equal(ConnectionSupervisorState.Online, f.Supervisor.Snapshot.State);
        if (mode == "conflict") Assert.Equal(RegionRouteRestorationOutcome.Conflict, result.Outcomes[0].Restoration);
        else Assert.DoesNotContain(result.Names, n => n.ExactName == "Area");
    }

    [Fact]
    public async Task RegionWireImplicitContactTableFullIsTerminalAndDoesNotEraseEarlierEvidence()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(true, configure: server =>
        {
            server.FirmwareProtocolVersion = 14; server.AddRepeater(0xC1, 0, "Area");
            server.NeighborRepeaters.Add(0xC3); server.RejectRegionContactCreation = true;
        });
        await using var lease = f.Gateway.Acquire(f.Node, new NodeCommandTarget(), CancellationToken);
        var result = await WireDiscovery(f).DiscoverAsync(lease.Owner, WireDiscoveryOptions(), cancellationToken: CancellationToken);
        Assert.Equal(RegionDiscoveryTargetStatus.ContactTableFull, result.Outcomes[1].Status);
        Assert.Single(result.Names); Assert.Single(await f.Storage.Regions.GetObservationsAsync(f.Node, CancellationToken));
        Assert.Equal(2, f.Server.RegionRequests.Count); Assert.False(f.Server.RepeaterContacts.ContainsKey(0xC3));
        Assert.Empty(f.Server.ContactUpdates);
    }
}

public sealed partial class BleIntegrationTests
{
    [Fact]
    public async Task RegionWireReconnectAcrossProfilesReadsExternalDefaultAndKeepsNodeCatalogueOffline()
    {
        await using var f = await Fixture.Create();
        var first = await f.Connect(configure: d => d.Server.FirmwareProtocolVersion = 14);
        var nodeA = f.Supervisor.Snapshot.NodeId!.Value; var service = new RegionService(f.Storage.Regions, TimeProvider.System, f.Gateway);
        var region = await service.CreatePublicAsync(nodeA, "Local A", "AreaA", Token);
        var binding = (await f.Storage.Directories.GetActiveChannelBindingAsync(nodeA, 0, Token))!;
        await service.AssignChannelAsync(nodeA, binding.ChannelId, FloodScopeMode.ExplicitRegion, region.Id, 0, Token);
        await using (var lease = f.Gateway.Acquire(nodeA, new NodeCommandTarget(), Token))
            Assert.Equal(RegionDefaultChangeOutcome.Applied, (await service.SetNodeDefaultAsync(lease.Owner, region.Definition, Token)).Outcome);
        await f.Supervisor.DisconnectAsync(Token);
        Assert.Single(await service.GetRegionsAsync(nodeA, cancellationToken: Token));
        Assert.Equal(RegionSnapshotFreshness.Offline, (await f.Storage.Regions.GetNodeSnapshotAsync(nodeA, Token))!.Freshness);
        var external = RegionDefinition.Public("External");
        var second = await f.Connect(ConnectionTransportKind.Tcp, configure: d =>
        {
            d.Server.FirmwareProtocolVersion = 14; var frame = new byte[48]; frame[0] = (byte)PacketType.DefaultFloodScope;
            System.Text.Encoding.UTF8.GetBytes(external.Name).CopyTo(frame, 1); external.Key.ToArray().CopyTo(frame, 32); d.Server.DefaultFloodScopeResponse = frame;
        });
        Assert.Equal(nodeA, f.Supervisor.Snapshot.NodeId); Assert.Equal("External", f.Gateway.GetDefaultRegion(nodeA)!.Value.Name);
        Assert.Equal(region.Id, (await service.GetChannelPreferenceAsync(nodeA, binding.ChannelId, Token)).RegionId);
        await f.Supervisor.DisconnectAsync(Token);
        var third = await f.Connect(suffix: 99, configure: d => d.Server.FirmwareProtocolVersion = 14);
        var nodeB = f.Supervisor.Snapshot.NodeId!.Value; Assert.NotEqual(nodeA, nodeB);
        Assert.Empty(await service.GetRegionsAsync(nodeB, cancellationToken: Token));
        Assert.Equal(NodeFloodScopeState.KnownEmpty, f.Gateway.GetDefaultRegion(nodeB)!.Value.State);
        await f.Supervisor.ShutdownAsync(Token);
        foreach (var device in new[] { first, second, third })
            Assert.DoesNotContain(device.Commands, command => command[0] is (byte)CommandType.SendTextMessage or (byte)CommandType.SendChannelTextMessage or (byte)CommandType.SendAnonymousRequest);
        foreach (var device in new[] { second, third })
            Assert.DoesNotContain(device.Commands, command => command[0] is (byte)CommandType.SetDefaultFloodScope or (byte)CommandType.SetFloodScopeKey);
    }
}
