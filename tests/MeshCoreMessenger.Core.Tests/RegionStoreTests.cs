using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class RegionStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-16T12:00:00Z");
    private static RegionCatalogEntry Entry(Guid node, string name = "A", string label = "Label") =>
        new(Guid.NewGuid(), node, label, RegionDefinition.Public(name), RegionProvenance.Manual, Now, Now);

    [Fact]
    public async Task CatalogueDeduplicatesKeysAllowsLabelsAndPersistsWithoutCrossNodeReads()
    {
        await using var f = await Context.Create();
        var a = await f.Store.AddRegionAsync(Entry(f.Node), Token);
        Assert.Equal(a.Id, (await f.Store.AddRegionAsync(Entry(f.Node, "#A", "Different label"), Token)).Id);
        var b = await f.Store.AddRegionAsync(Entry(f.Node, "B"), Token);
        var other = await f.Store.AddRegionAsync(Entry(f.OtherNode), Token);
        Assert.Null(await f.Store.GetRegionAsync(f.Node, other.Id, Token));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Store.SetRegionLabelAsync(f.OtherNode, a.Id, "bad", Now, Token));
        await f.Store.SetRegionLabelAsync(f.Node, a.Id, "Renamed", Now.AddMinutes(1), Token);
        await f.Store.SetRegionArchivedAsync(f.Node, a.Id, true, Now.AddMinutes(2), Token);
        Assert.Equal(b.Id, Assert.Single(await f.Store.GetRegionsAsync(f.Node, cancellationToken: Token)).Id);
        await f.Storage.DisposeAsync();
        await using var reopened = await LocalStorage.OpenAsync(f.Temporary.Paths, Token);
        var archived = await reopened.Regions.GetRegionAsync(f.Node, a.Id, Token);
        Assert.True(archived!.Archived); Assert.Equal("Renamed", archived.Label); Assert.Equal(a.KeyFingerprint, archived.KeyFingerprint);
        Assert.Equal(2, (await reopened.Regions.GetRegionsAsync(f.Node, true, Token)).Count);
        await reopened.Regions.DeleteRegionAsync(f.Node, a.Id, Token);
        Assert.Null(await reopened.Regions.GetRegionAsync(f.Node, a.Id, Token));
    }

    [Fact]
    public async Task PreferencesUseCasAndArchiveKeepsReferencesWhileDeleteAndCrossNodeWritesFail()
    {
        await using var f = await Context.Create(); var a = await f.Store.AddRegionAsync(Entry(f.Node), Token);
        var initial = await f.Store.GetChannelPreferenceAsync(f.Node, f.Channel, Token);
        Assert.Equal(FloodScopeMode.InheritDefault, initial.Mode); Assert.Equal(0, initial.Revision);
        var changed = 0; var observations = new List<Task<ChannelRegionPreference>>();
        f.Store.Changed += (_, commit) => { changed++; observations.Add(f.Store.GetChannelPreferenceAsync(commit.NodeId, f.Channel, Token)); };
        f.Store.Changed += (_, _) => throw new Exception("A broken observer must not invalidate a commit");
        var assigned = await f.Store.SetChannelPreferenceAsync(new(f.Node, f.Channel, FloodScopeMode.ExplicitRegion, a.Id, 0), Token);
        Assert.Equal(1, assigned.Revision); Assert.Equal(1, (await observations.Single()).Revision);
        await Assert.ThrowsAsync<RegionPreferenceConflictException>(() => f.Store.SetChannelPreferenceAsync(new(f.Node, f.Channel, FloodScopeMode.ForceUnscoped, null, 0), Token));
        Assert.Equal(1, changed);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Store.SetChannelPreferenceAsync(new(f.OtherNode, f.OtherChannel, FloodScopeMode.ExplicitRegion, a.Id, 0), Token));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Store.GetChannelPreferenceAsync(f.Node, f.OtherChannel, Token));
        await f.Store.SetRegionArchivedAsync(f.Node, a.Id, true, Now, Token);
        Assert.Equal(a.Id, (await f.Store.GetChannelPreferenceAsync(f.Node, f.Channel, Token)).RegionId);
        Assert.Single(await f.Store.GetRegionAssignmentsAsync(f.Node, a.Id, Token));
        await Assert.ThrowsAsync<RegionInUseException>(() => f.Store.DeleteRegionAsync(f.Node, a.Id, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.SetChannelPreferenceAsync(new(f.Node, f.ExtraChannel, FloodScopeMode.ExplicitRegion, a.Id, 0), Token));
        await f.Store.SetChannelPreferenceAsync(new(f.Node, f.Channel, FloodScopeMode.InheritDefault, null, 1), Token);
        await f.Store.DeleteRegionAsync(f.Node, a.Id, Token);
        await Task.WhenAll(observations);
    }

    [Fact]
    public async Task DefaultReadbackCacheIsOfflineAndIgnoresOlderObservation()
    {
        await using var f = await Context.Create();
        var key = new RegionKey(Enumerable.Repeat((byte)0xA7, 16).ToArray());
        var snapshot = new NodeRegionSnapshot(f.Node, f.Session, 1, Now, RegionSupport.Supported,
            NodeFloodScope.FromPreferences("$custom", key), RegionSnapshotFreshness.CurrentSession);
        await f.Store.SaveNodeSnapshotAsync(snapshot, Token);
        await f.Store.SaveNodeSnapshotAsync(new(f.Node, f.Session, 1, Now.AddMinutes(-1), RegionSupport.Supported,
            NodeFloodScope.KnownEmpty, RegionSnapshotFreshness.CurrentSession), Token);
        var stored = await f.Store.GetNodeSnapshotAsync(f.Node, Token);
        Assert.Equal(RegionSnapshotFreshness.Offline, stored!.Freshness);
        Assert.Equal("$custom", stored.Value.Name); Assert.Equal(key, stored.Value.Key); Assert.False(stored.IsCurrent(f.Session, 1));
        Assert.Null(await f.Store.GetNodeSnapshotAsync(f.OtherNode, Token));
        await Assert.ThrowsAsync<SqliteException>(() => f.Store.SaveNodeSnapshotAsync(new(f.OtherNode, f.Session, 1, Now,
            RegionSupport.Supported, NodeFloodScope.KnownEmpty, RegionSnapshotFreshness.CurrentSession), Token));
    }

    [Fact]
    public async Task ObservationsKeepMultipleSourcesAndBoundRetentionWithoutChangingCatalogue()
    {
        await using var f = await Context.Create(); var region = await f.Store.AddRegionAsync(Entry(f.Node), Token);
        var one = new RegionObservation(f.Node, new byte[32], "A", RegionObservationSource.Neighbor, Now, Now);
        var two = new RegionObservation(f.Node, Enumerable.Repeat((byte)1, 32).ToArray(), "A", RegionObservationSource.KnownRepeater, Now, Now);
        await f.Store.SaveObservationsAsync([one, two], 10, Now.AddDays(-1), Token);
        await f.Store.SaveObservationsAsync([one], 10, Now.AddDays(-1), Token);
        Assert.Equal(2, (await f.Store.GetObservationsAsync(f.Node, Token)).Count);
        var updated = new RegionObservation(f.Node, new byte[32], "A", RegionObservationSource.Neighbor, Now.AddMinutes(1), Now.AddMinutes(1));
        await f.Store.SaveObservationsAsync([updated], 1, Now.AddSeconds(1), Token);
        var last = Assert.Single(await f.Store.GetObservationsAsync(f.Node, Token));
        Assert.Equal(Now, last.FirstSeenUtc); Assert.Equal(Now.AddMinutes(1), last.LastSeenUtc);
        Assert.Equal(region.Id, Assert.Single(await f.Store.GetRegionsAsync(f.Node, cancellationToken: Token)).Id);
        Assert.Empty(await f.Store.GetObservationsAsync(f.OtherNode, Token));
    }

    [Fact]
    public async Task WriterFailureRollsBackAndNeverPublishesFailedMutation()
    {
        await using var f = await Context.Create(); var changed = 0; f.Store.Changed += (_, _) => changed++;
        using (var connection = SqliteDatabase.CreateConnection(f.Temporary.Paths.DatabasePath, SqliteOpenMode.ReadWrite))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER FailRegionInsert BEFORE INSERT ON Regions BEGIN SELECT RAISE(ABORT,'injected region store failure'); END;";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<SqliteException>(() => f.Store.AddRegionAsync(Entry(f.Node), Token));
        Assert.Equal(0, changed); Assert.Empty(await f.Store.GetRegionsAsync(f.Node, cancellationToken: Token));
        var valid = new RegionObservation(f.Node, new byte[32], "A", RegionObservationSource.Neighbor, Now, Now);
        var invalid = new RegionObservation(Guid.NewGuid(), new byte[32], "A", RegionObservationSource.Neighbor, Now, Now);
        await Assert.ThrowsAsync<SqliteException>(() => f.Store.SaveObservationsAsync([valid, invalid], 10, Now.AddDays(-1), Token));
        Assert.Empty(await f.Store.GetObservationsAsync(f.Node, Token)); Assert.Equal(0, changed);
    }

    internal sealed class Context : IAsyncDisposable
    {
        internal required RegionTestStorage Temporary;
        internal LocalStorage Storage => Temporary.Storage;
        internal IRegionStore Store => Storage.Regions;
        internal Guid Node, OtherNode, Session, Channel, ExtraChannel, OtherChannel;
        internal static async Task<Context> Create()
        {
            var temp = await RegionTestStorage.CreateAsync(Token);
            var manager = new ConnectionProfileManager(temp.Storage.ConnectionProfiles, temp.Storage.Settings, TimeProvider.System);
            var profile = await manager.SaveAsync(new() { Name = "TCP", Transport = ConnectionTransportKind.Tcp, TcpHost = "localhost", TcpPort = 5000 }, Token);
            var node = await temp.Storage.Nodes.FindOrCreateAsync(new byte[32], "A", Now, Token);
            var other = await temp.Storage.Nodes.FindOrCreateAsync(Enumerable.Repeat((byte)1, 32).ToArray(), "B", Now, Token);
            var session = Guid.NewGuid(); var foreignSession = Guid.NewGuid();
            await temp.Storage.Sessions.CreateAsync(new(session, profile.Id, node.Id, Now, null, null), Token);
            await temp.Storage.Sessions.CreateAsync(new(foreignSession, profile.Id, other.Id, Now, null, null), Token);
            var channels = await temp.Storage.Directories.ApplySnapshotAsync(node.Id, session, [],
                [new(0, "One", SHA256.HashData(new byte[16]), ChannelAccessKind.Unknown),
                 new(1, "Two", SHA256.HashData(new byte[16] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }), ChannelAccessKind.Unknown)], Now, Token);
            var foreign = await temp.Storage.Directories.ApplySnapshotAsync(other.Id, foreignSession, [],
                [new(0, "Foreign", SHA256.HashData(new byte[16]), ChannelAccessKind.Unknown)], Now, Token);
            return new() { Temporary = temp, Node = node.Id, OtherNode = other.Id, Session = session,
                Channel = channels.ActiveBindings.Single(b => b.Slot == 0).ChannelId, ExtraChannel = channels.ActiveBindings.Single(b => b.Slot == 1).ChannelId,
                OtherChannel = foreign.ActiveBindings.Single().ChannelId };
        }
        public ValueTask DisposeAsync() => Temporary.DisposeAsync();
    }
}
