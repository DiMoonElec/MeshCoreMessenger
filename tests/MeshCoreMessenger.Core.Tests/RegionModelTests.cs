using System.Security.Cryptography;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class RegionModelTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;
    private static readonly Guid Node = Guid.NewGuid();
    private static readonly Guid Channel = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-16T12:00:00Z");

    [Fact]
    public void ModeReferenceAndRevisionMustAgree()
    {
        foreach (var mode in new[] { FloodScopeMode.InheritDefault, FloodScopeMode.ForceUnscoped })
        {
            Assert.Null(new ChannelRegionPreference(Node, Channel, mode, null, 0).RegionId);
            Assert.Throws<ArgumentException>(() => new ChannelRegionPreference(Node, Channel, mode, Guid.NewGuid(), 1));
        }
        Assert.Throws<ArgumentException>(() => new ChannelRegionPreference(Node, Channel, FloodScopeMode.ExplicitRegion, null, 1));
        Assert.Throws<ArgumentException>(() => new ChannelRegionPreference(Node, Channel, FloodScopeMode.ExplicitRegion, Guid.Empty, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelRegionPreference(Node, Channel, FloodScopeMode.InheritDefault, null, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelRegionPreference(Node, Channel, (FloodScopeMode)99, null, 0));
    }

    [Fact]
    public void CatalogueAndAttemptOwnKeysAndDoNotPrintThem()
    {
        var bytes = Enumerable.Repeat((byte)0xA7, 16).ToArray();
        var definition = RegionDefinition.Custom("$private", new RegionKey(bytes));
        var entry = new RegionCatalogEntry(Guid.NewGuid(), Node, "Local label", definition, RegionProvenance.Manual, Now, Now);
        var snapshot = new ResolvedRegionScope(Node, Guid.NewGuid(), 1, FloodScopeMode.ExplicitRegion, 3,
            StoredRegionScopeState.KnownScoped, definition.Key, definition.Name, 160);
        bytes[0] = 0; var exported = snapshot.Key!.ToArray(); exported[1] = 0;
        Assert.All(entry.Definition.Key.ToArray(), b => Assert.Equal((byte)0xA7, b));
        Assert.Equal(entry.KeyFingerprint, snapshot.KeyFingerprint);
        Assert.True(snapshot.CanReplayExactly);
        Assert.DoesNotContain(new string('A', 8), entry.ToString());
        Assert.DoesNotContain("A7A7", snapshot.ToString());
        var renamed = new RegionCatalogEntry(entry.Id, Node, "Changed label", definition, entry.Provenance, Now, Now.AddMinutes(1));
        Assert.Equal(entry.KeyFingerprint, renamed.KeyFingerprint);
        Assert.Equal(entry.ProtocolName, renamed.ProtocolName);
    }

    [Fact]
    public void UnknownLegacyAndOfflineNeverClaimCurrentOrReplayableScope()
    {
        var session = Guid.NewGuid();
        var unknown = new NodeRegionSnapshot(Node, null, null, null, RegionSupport.Unknown, NodeFloodScope.Unknown, RegionSnapshotFreshness.Unknown);
        var empty = new NodeRegionSnapshot(Node, session, 1, Now, RegionSupport.Supported, NodeFloodScope.KnownEmpty, RegionSnapshotFreshness.CurrentSession);
        Assert.NotEqual(unknown.Value.State, empty.Value.State);
        Assert.True(empty.IsCurrent(session, 1)); Assert.False(empty.IsCurrent(session, 2));
        var offline = new NodeRegionSnapshot(Node, session, 1, Now, empty.Support, empty.Value, RegionSnapshotFreshness.Offline);
        Assert.False(offline.IsCurrent(session, 1));
        foreach (var state in new[] { StoredRegionScopeState.LegacyUnknown, StoredRegionScopeState.Unknown })
            Assert.False(new ResolvedRegionScope(Node, null, null, FloodScopeMode.InheritDefault, 0, state, null, null, null).CanReplayExactly);
        Assert.Throws<ArgumentException>(() => new ResolvedRegionScope(Node, session, 1, FloodScopeMode.ExplicitRegion, 0,
            StoredRegionScopeState.KnownScoped, null, "A", 160));
        Assert.Throws<ArgumentException>(() => new NodeRegionSnapshot(Node, null, null, Now, RegionSupport.Supported,
            NodeFloodScope.KnownEmpty, RegionSnapshotFreshness.CurrentSession));
    }

    [Fact]
    public void DiscoveryOwnsFullKeyAndPreservesPrivateNamesWithoutDerivation()
    {
        var key = Enumerable.Repeat((byte)1, 32).ToArray();
        var observation = new RegionObservation(Node, key, "$private", RegionObservationSource.Neighbor, Now, Now);
        key[0] = 2; var output = observation.GetRepeaterPublicKey(); output[1] = 3;
        Assert.All(observation.GetRepeaterPublicKey(), b => Assert.Equal((byte)1, b));
        Assert.Equal("$private", observation.ProtocolName);
        Assert.Throws<ArgumentException>(() => new RegionObservation(Node, new byte[8], "A", RegionObservationSource.Neighbor, Now, Now));
    }

    [Fact]
    public async Task SameNodeAcrossProfilesAndRenamedMovedChannelKeepRegionPreferenceIdentity()
    {
        await using var temporary = await RegionTestStorage.CreateAsync(CancellationToken);
        var storage = temporary.Storage;
        var profiles = new ConnectionProfileManager(storage.ConnectionProfiles, storage.Settings, TimeProvider.System);
        var a = await profiles.SaveAsync(new() { Name = "TCP", Transport = ConnectionTransportKind.Tcp, TcpHost = "localhost", TcpPort = 5000 }, CancellationToken);
        var b = await profiles.SaveAsync(new() { Name = "BLE", Transport = ConnectionTransportKind.Ble, BleDeviceId = "fixture" }, CancellationToken);
        var node = await storage.Nodes.FindOrCreateAsync(new byte[32], "Node", Now, CancellationToken);
        var same = await storage.Nodes.FindOrCreateAsync(new byte[32], "Renamed", Now.AddMinutes(1), CancellationToken);
        var other = await storage.Nodes.FindOrCreateAsync(Enumerable.Repeat((byte)1, 32).ToArray(), "Node", Now, CancellationToken);
        Assert.Equal(node.Id, same.Id); Assert.NotEqual(node.Id, other.Id);
        var s1 = new SessionRecord(Guid.NewGuid(), a.Id, node.Id, Now, null, null);
        var s2 = new SessionRecord(Guid.NewGuid(), b.Id, node.Id, Now, null, null);
        var s3 = new SessionRecord(Guid.NewGuid(), a.Id, other.Id, Now, null, null);
        foreach (var session in new[] { s1, s2, s3 }) await storage.Sessions.CreateAsync(session, CancellationToken);
        var fingerprint = SHA256.HashData(new byte[16]);
        var first = await storage.Directories.ApplySnapshotAsync(node.Id, s1.Id, [],
            [new(0, "First", fingerprint, ChannelAccessKind.Unknown)], Now, CancellationToken);
        var moved = await storage.Directories.ApplySnapshotAsync(node.Id, s2.Id, [],
            [new(1, "Renamed", fingerprint, ChannelAccessKind.Unknown)], Now.AddMinutes(1), CancellationToken);
        var foreign = await storage.Directories.ApplySnapshotAsync(other.Id, s3.Id, [],
            [new(0, "First", fingerprint, ChannelAccessKind.Unknown)], Now, CancellationToken);
        var preference = new ChannelRegionPreference(node.Id, first.ActiveBindings.Single().ChannelId, FloodScopeMode.ForceUnscoped, null, 2);
        Assert.Equal(preference.ChannelId, moved.ActiveBindings.Single(binding => binding.Slot == 1).ChannelId);
        Assert.NotEqual(preference.ChannelId, foreign.ActiveBindings.Single().ChannelId);
    }
}
