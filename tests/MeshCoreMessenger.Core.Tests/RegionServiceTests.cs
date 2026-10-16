using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class RegionServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-16T13:00:00Z");
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    [Fact]
    public async Task OfflineCatalogueCreateRenameReplacementAndCustomImportDoNotRewriteAssignments()
    {
        await using var f = await RegionStoreTests.Context.Create();
        IRegionService service = new RegionService(f.Store, new Clock());
        var a = await service.CreatePublicAsync(f.Node, "Original", "A", Token);
        await service.AssignChannelAsync(f.Node, f.Channel, FloodScopeMode.ExplicitRegion, a.Id, 0, Token);
        await service.RenameLabelAsync(f.Node, a.Id, "Renamed", Token);
        var replacement = await service.CreateReplacementAsync(f.Node, a.Id, RegionDefinition.Public("B"), Token);
        Assert.NotEqual(a.Id, replacement.Id); Assert.NotEqual(a.KeyFingerprint, replacement.KeyFingerprint);
        Assert.Equal("Renamed", replacement.Label);
        Assert.Equal("A", (await f.Store.GetRegionAsync(f.Node, a.Id, Token))!.ProtocolName);
        Assert.Equal(a.Id, (await service.GetChannelPreferenceAsync(f.Node, f.Channel, Token)).RegionId);
        var key = Enumerable.Repeat((byte)0xA7, 16).ToArray();
        var custom = await service.ImportCustomAsync(f.Node, "Private", "$private", key, Token); key[0] = 0;
        Assert.All(custom.Definition.Key.ToArray(), value => Assert.Equal((byte)0xA7, value));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateReplacementAsync(f.Node, custom.Id,
            RegionDefinition.Custom("$renamed", custom.Definition.Key), Token));
        Assert.Equal(3, (await service.GetRegionsAsync(f.Node, cancellationToken: Token)).Count);
    }

    [Fact]
    public async Task DuplicateSlotsSharePreferenceAndReplacingSecretProjectsInheritedPolicy()
    {
        await using var f = await RegionStoreTests.Context.Create(); var service = new RegionService(f.Store, new Clock());
        var region = await service.CreatePublicAsync(f.Node, "A", "A", Token);
        await service.AssignChannelAsync(f.Node, f.Channel, FloodScopeMode.ExplicitRegion, region.Id, 0, Token);
        var secret = SHA256.HashData(new byte[16]); var other = SHA256.HashData(Enumerable.Repeat((byte)9, 16).ToArray());
        var snapshot = await f.Storage.Directories.ApplySnapshotAsync(f.Node, f.Session, [],
            [new(0, "First", secret, ChannelAccessKind.Unknown), new(2, "Duplicate", secret, ChannelAccessKind.Unknown)], Now, Token);
        Assert.Equal(f.Channel, snapshot.ActiveBindings.Single(binding => binding.Slot == 2).ChannelId);
        Assert.Equal(region.Id, (await service.GetChannelPreferenceAsync(f.Node, f.Channel, Token)).RegionId);
        var changed = await f.Storage.Directories.ApplySnapshotAsync(f.Node, f.Session, [],
            [new(0, "First", secret, ChannelAccessKind.Unknown), new(2, "Changed", other, ChannelAccessKind.Unknown)], Now.AddMinutes(1), Token);
        await f.Storage.Directories.CommitPendingChannelTransitionsAsync(changed.PendingChannelTransitions, Now.AddMinutes(1), Token);
        var binding = await f.Storage.Directories.GetActiveChannelBindingAsync(f.Node, 2, Token);
        Assert.NotEqual(f.Channel, binding!.ChannelId);
        var preference = await service.GetChannelPreferenceAsync(f.Node, binding.ChannelId, Token);
        Assert.Equal(FloodScopeMode.InheritDefault, preference.Mode); Assert.Equal(0, preference.Revision);
    }

    [Fact]
    public async Task ReferencedDeleteReturnsAssignmentsAndExplicitReplacementAllowsDeletion()
    {
        await using var f = await RegionStoreTests.Context.Create(); var service = new RegionService(f.Store, new Clock());
        var region = await service.CreatePublicAsync(f.Node, "A", "A", Token);
        await service.AssignChannelAsync(f.Node, f.Channel, FloodScopeMode.ExplicitRegion, region.Id, 0, Token);
        var error = await Assert.ThrowsAsync<RegionInUseException>(() => service.DeleteAsync(f.Node, region.Id, Token));
        Assert.Equal(f.Channel, Assert.Single(error.ChannelIds));
        await service.SetArchivedAsync(f.Node, region.Id, true, Token);
        Assert.Equal(region.Id, (await service.GetChannelPreferenceAsync(f.Node, f.Channel, Token)).RegionId);
        await service.AssignChannelAsync(f.Node, f.Channel, FloodScopeMode.ForceUnscoped, null, 1, Token);
        await service.DeleteAsync(f.Node, region.Id, Token);
        Assert.Empty(await service.GetRegionsAsync(f.Node, true, Token));
    }

    [Fact]
    public async Task StorageFailureHasNoSuccessEventOrInMemoryCatalogueMutation()
    {
        await using var f = await RegionStoreTests.Context.Create(); var service = new RegionService(f.Store, new Clock());
        var events = 0; service.Changed += (_, _) => events++;
        using (var connection = SqliteDatabase.CreateConnection(f.Temporary.Paths.DatabasePath, SqliteOpenMode.ReadWrite))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER FailRegionService BEFORE INSERT ON Regions BEGIN SELECT RAISE(ABORT,'injected service failure'); END;";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<SqliteException>(() => service.CreatePublicAsync(f.Node, "A", "A", Token));
        Assert.Equal(0, events); Assert.Empty(await service.GetRegionsAsync(f.Node, cancellationToken: Token));
    }
}
