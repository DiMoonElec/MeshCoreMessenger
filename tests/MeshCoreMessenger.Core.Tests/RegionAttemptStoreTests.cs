using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using MeshCoreSharp.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class OutgoingMessageStoreTests
{
    private static ResolvedRegionScope CapturedScope(PrepareOutgoingMessage request, string name = "$private") => new(
        request.NodeId, request.SessionId, 42, FloodScopeMode.ExplicitRegion, 7, StoredRegionScopeState.KnownScoped,
        new RegionKey(Enumerable.Repeat((byte)0xA7, 16).ToArray()), name, request.MaxUtf8Bytes);

    [Fact]
    public async Task RegionScopePreparationIsAtomicIdempotentAndImmutable()
    {
        await using var f = await Fixture.CreateAsync(); var request = f.Request();
        request = request with { Scope = CapturedScope(request) };
        var prepared = await f.Storage.OutgoingMessages.PrepareAsync(request, CancellationToken);
        Assert.Equal(prepared, await f.Storage.OutgoingMessages.PrepareAsync(request, CancellationToken));
        var scope = await f.Storage.OutgoingMessages.GetAttemptScopeAsync(f.NodeId, prepared.Attempt.Id, CancellationToken);
        Assert.True(scope.CanReplayExactly); Assert.Equal("$private", scope.Name); Assert.Equal(7, scope.PolicyRevision);
        Assert.Equal(request.Scope.KeyFingerprint, scope.KeyFingerprint);
        Assert.All(scope.Key!.ToArray(), b => Assert.Equal((byte)0xA7, b));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.PrepareAsync(request with { Scope = CapturedScope(request, "$other") }, CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.PrepareAsync(request with { Scope = null }, CancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Storage.OutgoingMessages.GetAttemptScopeAsync(Guid.NewGuid(), prepared.Attempt.Id, CancellationToken));
        Assert.Throws<SqliteException>(() => f.Execute($"UPDATE SendAttemptScopes SET Name='changed' WHERE AttemptId='{prepared.Attempt.Id:D}'"));
        Assert.DoesNotContain("A7A7", scope.ToString());
    }

    [Fact]
    public async Task FailedScopeInsertRollsBackMessageAndAttemptAndDoesNotNotify()
    {
        await using var f = await Fixture.CreateAsync(); var request = f.Request(); var notifications = 0;
        f.Storage.OutgoingMessages.MessageCommitted += (_, _) => notifications++;
        f.Execute("CREATE TRIGGER RejectScope BEFORE INSERT ON SendAttemptScopes BEGIN SELECT RAISE(ABORT,'injected scope failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => f.Storage.OutgoingMessages.PrepareAsync(request with { Scope = CapturedScope(request) }, CancellationToken));
        Assert.Equal(0, notifications);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Storage.OutgoingMessages.GetAsync(f.NodeId, request.OperationId, CancellationToken));
        f.Execute("DROP TRIGGER RejectScope;");
        Assert.NotNull(await f.Storage.OutgoingMessages.PrepareAsync(request with { Scope = CapturedScope(request) }, CancellationToken));
    }

    [Fact]
    public async Task RegionScopeSurvivesCatalogueDeletionAndStartupRecoveryWithoutCommands()
    {
        await using var f = await Fixture.CreateAsync(); var request = f.Request();
        var entry = await f.Storage.Regions.AddRegionAsync(new(Guid.NewGuid(), f.NodeId, "Local", RegionDefinition.Custom("$private", CapturedScope(request).Key!),
            RegionProvenance.Manual, request.PreparedUtc, request.PreparedUtc), CancellationToken);
        var prepared = await f.Storage.OutgoingMessages.PrepareAsync(request with { Scope = CapturedScope(request) }, CancellationToken);
        await f.Storage.Regions.SetRegionLabelAsync(f.NodeId, entry.Id, "Renamed", request.PreparedUtc, CancellationToken);
        await f.Storage.Regions.SetRegionArchivedAsync(f.NodeId, entry.Id, true, request.PreparedUtc, CancellationToken);
        await f.Storage.Regions.DeleteRegionAsync(f.NodeId, entry.Id, CancellationToken);
        await f.Move(prepared, SendAttemptState.Prepared, SendAttemptState.Sending);
        await f.Reopen();
        Assert.Equal(SendAttemptState.Unknown, Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, prepared.MessageId, CancellationToken)).State);
        var saved = await f.Storage.OutgoingMessages.GetAttemptScopeAsync(f.NodeId, prepared.Attempt.Id, CancellationToken);
        Assert.Equal("$private", saved.Name); Assert.Equal(CapturedScope(request).KeyFingerprint, saved.KeyFingerprint);
    }

    [Fact]
    public async Task ClearHistoryRemovesOnlyItsAttemptKeysAndPreservesOtherConversation()
    {
        await using var f = await Fixture.CreateAsync(); var a = f.Request(); var b = f.Request(channel: true);
        var first = await f.Storage.OutgoingMessages.PrepareAsync(a with { Scope = CapturedScope(a) }, CancellationToken);
        var second = await f.Storage.OutgoingMessages.PrepareAsync(b with { Scope = CapturedScope(b) }, CancellationToken);
        await f.Storage.HistoryClear.ClearAsync(f.NodeId, a.ConversationId, CancellationToken);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Storage.OutgoingMessages.GetAttemptScopeAsync(f.NodeId, first.Attempt.Id, CancellationToken));
        Assert.True((await f.Storage.OutgoingMessages.GetAttemptScopeAsync(f.NodeId, second.Attempt.Id, CancellationToken)).CanReplayExactly);
    }

    [Fact]
    public async Task PrivateRetriesRetainOriginalScopeAndRejectReplacement()
    {
        await using var f = await Fixture.CreateAsync(); var request = f.Request();
        var capture = CapturedScope(request);
        var message = await f.Storage.OutgoingMessages.PrepareAsync(request with { Scope = capture }, CancellationToken);
        await f.Storage.OutgoingMessages.BeginPrivateCycleAsync(new(f.NodeId, message.MessageId, f.SessionId,
            new(PrivateRepeatMode.SameTimestampIncrementAttempt), Route(true), PrivatePcTime), CancellationToken);
        var firstRequest = PrivatePreparation(f, message.MessageId) with { Scope = capture };
        var first = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(firstRequest, CancellationToken);
        Assert.Equal(first, await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(firstRequest, CancellationToken));
        await Expire(f, first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(
            PrivatePreparation(f, message.MessageId, 1) with { Scope = CapturedScope(request, "$other") }, CancellationToken));
        var second = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(PrivatePreparation(f, message.MessageId, 1), CancellationToken);
        Assert.Equal(capture.KeyFingerprint, (await f.Storage.OutgoingMessages.GetAttemptScopeAsync(f.NodeId, second.Message.Attempt.Id, CancellationToken)).KeyFingerprint);
    }

    [Fact]
    public async Task VersionEightHistoryGetsLegacyUnknownAndBackupWithoutInventedKey()
    {
        await using var f = await Fixture.CreateAsync();
        var prepared = await f.Storage.OutgoingMessages.PrepareAsync(f.Request(), CancellationToken);
        await f.Storage.DisposeAsync();
        f.Execute("DROP TABLE RegionDiscoveryOutcomes; DROP TABLE SendAttemptScopes; DELETE FROM SchemaMigrations WHERE Version>=9; PRAGMA user_version=8;");
        f.Storage = await LocalStorage.OpenAsync(f.Paths, CancellationToken);
        var scope = await f.Storage.OutgoingMessages.GetAttemptScopeAsync(f.NodeId, prepared.Attempt.Id, CancellationToken);
        Assert.Equal(StoredRegionScopeState.LegacyUnknown, scope.State); Assert.Null(scope.Key); Assert.False(scope.CanReplayExactly);
        var backup = Assert.Single(Directory.GetFiles(f.Paths.BackupsDirectory, "*.db"));
        using var original = SqliteDatabase.CreateConnection(backup, SqliteOpenMode.ReadOnly); original.Open();
        Assert.Equal(8, SqliteDatabase.GetUserVersion(original));
    }

    [Fact]
    public async Task ChannelRepeatKeepsKnownUnscopedCaptureAndRejectsNewRegion()
    {
        await using var f = await Fixture.CreateAsync(); var request = f.Request(channel: true);
        var capture = new ResolvedRegionScope(f.NodeId, f.SessionId, 42, FloodScopeMode.ForceUnscoped, 3,
            StoredRegionScopeState.KnownUnscoped, null, null, request.MaxUtf8Bytes);
        var first = await f.Storage.OutgoingMessages.PrepareAsync(request with { Scope = capture }, CancellationToken);
        var repeat = new PrepareChannelRepeat(f.NodeId, first.MessageId, f.SessionId, request.Recipient, 1, request.PreparedUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.PrepareChannelRepeatAsync(repeat with { Scope = CapturedScope(request) }, CancellationToken));
        var next = await f.Storage.OutgoingMessages.PrepareChannelRepeatAsync(repeat, CancellationToken);
        var saved = await f.Storage.OutgoingMessages.GetAttemptScopeAsync(f.NodeId, next.Attempt.Id, CancellationToken);
        Assert.Equal(StoredRegionScopeState.KnownUnscoped, saved.State); Assert.True(saved.CanReplayExactly); Assert.Null(saved.Key);
    }

    [Fact]
    public async Task ScopeOwnershipRejectsAnotherRealNodeAndSessionBeforePreparation()
    {
        await using var f = await Fixture.CreateAsync(); var request = f.Request();
        var otherNode = await f.Storage.Nodes.FindOrCreateAsync(Enumerable.Repeat((byte)9,32).ToArray(), "Other", request.PreparedUtc, CancellationToken);
        var session = await f.Storage.Sessions.GetAsync(f.SessionId, CancellationToken); var otherSession = Guid.NewGuid();
        await f.Storage.Sessions.CreateAsync(new(otherSession, session!.ConnectionProfileId, otherNode.Id, request.PreparedUtc, null, null), CancellationToken);
        var wrong = new ResolvedRegionScope(otherNode.Id, otherSession, 43, FloodScopeMode.ForceUnscoped, 0,
            StoredRegionScopeState.KnownUnscoped, null, null, request.MaxUtf8Bytes);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Storage.OutgoingMessages.PrepareAsync(request with { Scope = wrong }, CancellationToken));
        var prepared = await f.Storage.OutgoingMessages.PrepareAsync(request, CancellationToken);
        using var connection = f.Open(); using var tx = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = $"DELETE FROM SendAttemptScopes WHERE AttemptId='{prepared.Attempt.Id:D}';"; command.ExecuteNonQuery();
        command.CommandText = $"INSERT INTO SendAttemptScopes(AttemptId,NodeId,SessionId,RequestedMode,PolicyRevision,State) VALUES('{prepared.Attempt.Id:D}','{otherNode.Id:D}','{otherSession:D}',0,0,0);";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery()); tx.Rollback();
    }

    [Fact]
    public void AttemptScopeMigrationFailureRollsBackSchemaAndVersion()
    {
        using var connection = SqliteDatabase.CreateConnection(":memory:", SqliteOpenMode.ReadWriteCreate);
        connection.Open(); SqliteDatabase.ConfigureReader(connection); DatabaseMigrator.ApplyPending(connection, 8);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER RejectScopeMigration BEFORE INSERT ON SchemaMigrations WHEN NEW.Version=9 BEGIN SELECT RAISE(ABORT,'injected migration failure'); END;";
        command.ExecuteNonQuery();
        Assert.Throws<DatabaseStorageException>(() => DatabaseMigrator.ApplyPending(connection));
        Assert.Equal(8, SqliteDatabase.GetUserVersion(connection));
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name IN('SendAttemptScopes','TR_SendAttemptScopes_Owner','TR_SendAttemptScopes_Immutable','IX_SendAttemptScopes_Node');";
        Assert.Equal(0L, command.ExecuteScalar());
    }
}
