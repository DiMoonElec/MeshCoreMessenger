using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    private sealed class DiscoveryFixture : IAsyncDisposable
    {
        internal required Fixture App;
        internal required SessionCommandOwner Owner;
        internal required RegionDiscoveryService Service;
        internal required Dictionary<string, Contact> Contacts;
        internal Client Client => App.Clients.Current!;
        internal static async Task<DiscoveryFixture> Create(int count = 4)
        {
            var app = await Fixture.CreateAsync();
            var contacts = Enumerable.Range(1, count).Select(i => Repeater((byte)i)).ToDictionary(c => c.PublicKeyHex);
            app.Clients.Configure = client =>
            {
                client.PreflightContacts = contacts.Values.ToArray();
                client.ContactRead = (key, _) => contacts.TryGetValue(Convert.ToHexString(key.Span), out var contact) ? Task.FromResult(contact) :
                    Task.FromException<Contact>(new MeshCoreCommandException(CommandType.GetContactByKey, MeshCoreErrorCode.NotFound));
                client.NeighborScan = (_, _) => Task.FromResult(new NeighborDiscoveryResult(1, [Neighbor(1), Neighbor(9, false)], true));
                client.ContactWrite = (configuration, _) =>
                {
                    var key = Convert.ToHexString(configuration.PublicKey.Span);
                    contacts[key] = new(configuration.PublicKey.ToArray(), configuration.AdvertisementType, configuration.Flags,
                        configuration.OutPathLength, configuration.OutPath.ToArray(), configuration.Name, configuration.LastAdvertTimestamp,
                        configuration.AdvertisementLatitude, configuration.AdvertisementLongitude, contacts[key].LastModified + 1);
                    client.PreflightContacts = contacts.Values.ToArray(); return Task.CompletedTask;
                };
            };
            await app.Connect(); await using var owner = app.Acquire();
            return new() { App = app, Owner = owner.Owner, Contacts = contacts,
                Service = new(app.Gateway, new RegionDiscoveryPreflight(app.Gateway, TimeProvider.System),
                    new RegionDiscoveryRouteWorkflow(app.Gateway, app.Storage.Directories, app.Operations, TimeProvider.System), app.Storage.Regions, TimeProvider.System) };
        }
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
    private sealed class DiscoveryProgress(Action<RegionDiscoveryProgress> report) : IProgress<RegionDiscoveryProgress>
    { public void Report(RegionDiscoveryProgress value) => report(value); }

    [Fact]
    public async Task DiscoveryUnionsSourcesKeepsTimeoutEmptyAndPrivateDistinctionsWithoutSavingCatalogue()
    {
        await using var f = await DiscoveryFixture.Create();
        f.Client.RegionRequest = (key, _, _) => key.Span[^1] switch
        {
            1 => Task.FromResult(new RegionNamesResponse(1, 2, ["Area", "$hidden", "*"])),
            2 => throw new MeshCoreTimeoutException("Remote", TimeSpan.FromMilliseconds(20)),
            3 => Task.FromResult(new RegionNamesResponse(2, 3, ["Area", "odd!"])),
            _ => Task.FromResult(new RegionNamesResponse(3, 4, [])),
        };
        var progress = new List<RegionDiscoveryProgress>();
        var result = await f.Service.DiscoverAsync(f.Owner, progress: new DiscoveryProgress(progress.Add), cancellationToken: CancellationToken);
        Assert.Equal(4, result.Outcomes.Count); Assert.All(result.Outcomes, o => Assert.True(o.Persisted));
        Assert.Equal(RegionDiscoveryTargetStatus.Timeout, result.Outcomes.Single(o => o.RepeaterPublicKeyHex.EndsWith("02")).Status);
        Assert.Equal(RegionDiscoveryTargetStatus.SuccessEmpty, result.Outcomes.Single(o => o.RepeaterPublicKeyHex.EndsWith("04")).Status);
        Assert.Equal(2, result.Names.Single(n => n.ExactName == "Area").SourcePublicKeys.Count);
        Assert.Equal(DiscoveredRegionNameKind.PrivateKeyRequired, result.Names.Single(n => n.ExactName == "$hidden").Kind);
        Assert.Equal(DiscoveredRegionNameKind.Unscoped, result.Names.Single(n => n.ExactName == "*").Kind);
        Assert.Equal(DiscoveredRegionNameKind.Unrecognized, result.Names.Single(n => n.ExactName == "odd!").Kind);
        Assert.True(result.NeighborCapacityReached); Assert.Equal(1, result.UnresolvedPrefixCount); Assert.True(result.IsPotentiallyIncomplete);
        Assert.Equal(1, f.Client.NeighborScanCount); Assert.Equal(4, f.Client.RegionRequestCount);
        Assert.Empty(await f.App.Storage.Regions.GetRegionsAsync(f.Owner.NodeId, cancellationToken: CancellationToken));
        Assert.Equal(4, (await f.App.Storage.Regions.GetDiscoveryOutcomesAsync(f.Owner.NodeId, CancellationToken)).Count);
        var observations = await f.App.Storage.Regions.GetObservationsAsync(f.Owner.NodeId, CancellationToken);
        Assert.Equal(5, observations.Count); Assert.Contains(observations, o => o.Source == RegionObservationSource.Neighbor);
        Assert.Contains(observations, o => o.Source == RegionObservationSource.KnownRepeater);
        Assert.Equal(RegionDiscoveryPhase.Completed, progress[^1].Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryCancelOrDeadlinePreservesCommittedSourcesAndNeverRetries(bool deadline)
    {
        await using var f = await DiscoveryFixture.Create(); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        var entered = NewGate();
        f.Client.RegionRequest = async (key, _, token) =>
        {
            if (key.Span[^1] == 2) { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            return new(1, 2, ["Area"]);
        };
        var task = f.Service.DiscoverAsync(f.Owner, new() { TotalTimeout = deadline ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(5) }, cancellationToken: cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.DiscoverAsync(f.Owner, cancellationToken: CancellationToken));
        if (!deadline) cancel.Cancel();
        var result = await task;
        Assert.Equal(!deadline, result.Cancelled); Assert.Equal(deadline, result.DeadlineReached);
        Assert.Equal(2, f.Client.RegionRequestCount); Assert.Equal(2, result.Outcomes.Count);
        Assert.Equal(deadline ? RegionDiscoveryTargetStatus.Timeout : RegionDiscoveryTargetStatus.Cancelled, result.Outcomes[^1].Status);
        Assert.Single(result.Names);
        Assert.Single(await f.App.Storage.Regions.GetObservationsAsync(f.Owner.NodeId, CancellationToken));
    }

    [Fact]
    public async Task DiscoveryConsentRequiresFreshPreparationAndRefreshesImplicitContactWithoutDeletingIt()
    {
        await using var f = await DiscoveryFixture.Create(1);
        var known = f.Contacts.Values.Single() with { OutPathLength = byte.MaxValue }; f.Contacts[known.PublicKeyHex] = known; f.Client.PreflightContacts = [known];
        f.Client.NeighborScan = (_, _) => Task.FromResult(new NeighborDiscoveryResult(1, [Neighbor(1), Neighbor(2)], false));
        f.Client.RegionRequest = (key, _, _) =>
        {
            var hex = Convert.ToHexString(key.Span);
            if (!f.Contacts.ContainsKey(hex)) { f.Contacts.Add(hex, Repeater(2)); f.Client.PreflightContacts = f.Contacts.Values.ToArray(); }
            return Task.FromResult(new RegionNamesResponse(1, 2, ["Area"]));
        };
        var result = await f.Service.DiscoverAsync(f.Owner, cancellationToken: CancellationToken);
        Assert.All(result.Outcomes, o => Assert.Equal(RegionDiscoveryTargetStatus.PreparationRequired, o.Status)); Assert.Equal(0, f.Client.RegionRequestCount);
        result = await f.Service.DiscoverAsync(f.Owner, new() { Consent = new(true, true) }, cancellationToken: CancellationToken);
        Assert.Equal(2, f.Client.RegionRequestCount);
        Assert.Contains(result.Outcomes, o => o.ContactCreation == RegionContactCreationOutcome.Observed);
        Assert.Contains(result.Outcomes, o => o.Restoration == RegionRouteRestorationOutcome.Restored);
        Assert.Equal(2, (await f.App.Storage.Directories.GetCurrentContactsByPrefixAsync(f.Owner.NodeId, RepeaterKey(1)[..6], CancellationToken)).Count);
        Assert.DoesNotContain(f.Client.Mutations, mutation => mutation == "remove");
    }

    [Fact]
    public async Task MalformedAndUnsupportedTargetsRemainDistinctAndMissingSelectedKeyIsNotInventedAsDirect()
    {
        await using var f = await DiscoveryFixture.Create(3);
        f.Client.RegionRequest = (key, _, _) => key.Span[^1] switch
        {
            1 => Task.FromResult(new RegionNamesResponse(1, 1, ["A", "", "B"])),
            2 => throw new MeshCoreCommandException(CommandType.SendAnonymousRequest, MeshCoreErrorCode.UnsupportedCommand),
            _ => Task.FromResult(new RegionNamesResponse(1, 1, ["Good"])),
        };
        var result = await f.Service.DiscoverAsync(f.Owner, new() { SelectedTargetPublicKeys = [Convert.ToHexString(RepeaterKey(1)), Convert.ToHexString(RepeaterKey(2)), Convert.ToHexString(RepeaterKey(8))] },
            new DiscoveryProgress(_ => throw new InvalidOperationException("observer failure")), CancellationToken);
        Assert.Contains(result.Outcomes, o => o.Status == RegionDiscoveryTargetStatus.Malformed);
        Assert.Contains(result.Outcomes, o => o.Status == RegionDiscoveryTargetStatus.Unsupported);
        Assert.Contains(result.Outcomes, o => o.Status == RegionDiscoveryTargetStatus.NoDirectPath);
        Assert.Equal(2, f.Client.RegionRequestCount); Assert.Empty(result.Names);
    }

    [Fact]
    public async Task StorageFailureStopsRemainingQueriesAndRetryDoesNotReplayMeshRequests()
    {
        await using var f = await DiscoveryFixture.Create(3);
        f.App.Execute($"CREATE TRIGGER RefuseOutcome BEFORE INSERT ON RegionDiscoveryOutcomes WHEN hex(NEW.RepeaterPublicKey)='{Convert.ToHexString(RepeaterKey(2))}' BEGIN SELECT RAISE(ABORT,'injected discovery failure'); END;");
        var result = await f.Service.DiscoverAsync(f.Owner, cancellationToken: CancellationToken);
        Assert.Equal(2, f.Client.RegionRequestCount); Assert.Equal(RegionDiscoveryTargetStatus.StorageFailed, result.Outcomes[^1].Status);
        Assert.True(result.Outcomes[0].Persisted); Assert.False(result.Outcomes[^1].Persisted); Assert.NotNull(result.Error);
        Assert.Single(await f.App.Storage.Regions.GetDiscoveryOutcomesAsync(f.Owner.NodeId, CancellationToken));
        Assert.Single(await f.App.Storage.Regions.GetObservationsAsync(f.Owner.NodeId, CancellationToken));
        f.App.Execute("DROP TRIGGER RefuseOutcome;"); await f.App.Outgoing.RetryAsync(CancellationToken);
        Assert.Equal(2, f.Client.RegionRequestCount);
    }

    [Fact]
    public async Task StaleSessionRejectsLateNamesAndNeverWritesToReplacementOwner()
    {
        await using var f = await DiscoveryFixture.Create(1); var entered = NewGate(); var release = NewGate();
        f.Client.RegionRequest = async (_, _, _) => { entered.TrySetResult(); await release.Task; return new(1, 2, ["late"]); };
        var task = f.Service.DiscoverAsync(f.Owner, cancellationToken: CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken);
        var stop = f.App.Supervisor.DisconnectAsync(CancellationToken);
        await Until(() => f.Client.State == MeshCoreSharp.MeshCoreConnectionState.Disconnected);
        release.TrySetResult(); var result = await task; await stop;
        Assert.True(result.SessionEnded); Assert.Empty(result.Names);
        Assert.Empty(await f.App.Storage.Regions.GetObservationsAsync(f.Owner.NodeId, CancellationToken));
        await f.App.Connect();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.DiscoverAsync(f.Owner, cancellationToken: CancellationToken));
    }
}

public sealed class RegionDiscoveryStorageTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task OutcomesAndNamesCommitAtomicallyWithRetentionAndNodeOwnership()
    {
        await using var f = await RegionStoreTests.Context.Create(); var now = DateTimeOffset.UtcNow; var key = new byte[32];
        RegionDiscoveryTargetOutcome Outcome(int i) => new(Guid.NewGuid(), f.Node, f.Session, 1, Convert.ToHexString(key), now.AddSeconds(i),
            RegionDiscoveryTargetStatus.SuccessPossiblyPartial, Array.AsReadOnly(new[] { "A" + i }));
        RegionObservation Observation(RegionDiscoveryTargetOutcome outcome) => new(f.Node, key, outcome.Names[0], RegionObservationSource.Neighbor, outcome.CompletedUtc, outcome.CompletedUtc);
        var outcomes = new[] { Outcome(1), Outcome(2), Outcome(3) };
        foreach (var outcome in outcomes) await f.Store.SaveDiscoveryOutcomeAsync(outcome, [Observation(outcome)], 1, 2, now.AddDays(-1), Token);
        Assert.Equal(2, (await f.Store.GetDiscoveryOutcomesAsync(f.Node, Token)).Count); Assert.Single(await f.Store.GetObservationsAsync(f.Node, Token));
        await f.Store.SaveDiscoveryOutcomeAsync(outcomes[2], [Observation(outcomes[2])], 1, 2, now.AddDays(-1), Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.SaveDiscoveryOutcomeAsync(outcomes[2] with { Status = RegionDiscoveryTargetStatus.Timeout }, [], 1, 2, now.AddDays(-1), Token));
        await Assert.ThrowsAsync<SqliteException>(() => f.Store.SaveDiscoveryOutcomeAsync(Outcome(4) with { NodeId = f.OtherNode }, [], 1, 2, now.AddDays(-1), Token));
        Assert.Empty(await f.Store.GetDiscoveryOutcomesAsync(f.OtherNode, Token));
        using (var c = SqliteDatabase.CreateConnection(f.Temporary.Paths.DatabasePath, SqliteOpenMode.ReadWrite))
        {
            c.Open(); using var command = c.CreateCommand(); command.CommandText = "CREATE TRIGGER RefuseEvidence BEFORE INSERT ON RegionObservations BEGIN SELECT RAISE(ABORT,'injected evidence failure'); END;"; command.ExecuteNonQuery();
        }
        var failed = Outcome(5);
        await Assert.ThrowsAsync<SqliteException>(() => f.Store.SaveDiscoveryOutcomeAsync(failed, [Observation(failed)], 1, 2, now.AddDays(-1), Token));
        Assert.DoesNotContain(await f.Store.GetDiscoveryOutcomesAsync(f.Node, Token), o => o.RunId == failed.RunId);
    }

    [Fact]
    public void DiscoveryMigrationFailureRollsBackTableIndexAndVersion()
    {
        using var c = SqliteDatabase.CreateConnection(":memory:", SqliteOpenMode.ReadWriteCreate); c.Open(); SqliteDatabase.ConfigureReader(c);
        DatabaseMigrator.ApplyPending(c, 9); using var command = c.CreateCommand();
        command.CommandText = "CREATE TRIGGER RefuseDiscoveryMigration BEFORE INSERT ON SchemaMigrations WHEN NEW.Version=10 BEGIN SELECT RAISE(ABORT,'injected migration failure'); END;"; command.ExecuteNonQuery();
        Assert.Throws<DatabaseStorageException>(() => DatabaseMigrator.ApplyPending(c)); Assert.Equal(9, SqliteDatabase.GetUserVersion(c));
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name IN('RegionDiscoveryOutcomes','IX_RegionDiscoveryOutcomes_Node_Completed');";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public async Task VersionNineUpgradeMakesBackupAndKeepsScopeSchemaIntact()
    {
        await using var temp = await RegionTestStorage.CreateAsync(Token, 9);
        var backup = Assert.Single(Directory.GetFiles(temp.Paths.BackupsDirectory, "*.db"));
        using var before = SqliteDatabase.CreateConnection(backup, SqliteOpenMode.ReadOnly); before.Open();
        Assert.Equal(9, SqliteDatabase.GetUserVersion(before));
        using var current = SqliteDatabase.CreateConnection(temp.Paths.DatabasePath, SqliteOpenMode.ReadOnly); current.Open();
        Assert.Equal(10, SqliteDatabase.GetUserVersion(current)); using var command = current.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name IN('SendAttemptScopes','RegionDiscoveryOutcomes');";
        Assert.Equal(2L, command.ExecuteScalar());
    }
}
