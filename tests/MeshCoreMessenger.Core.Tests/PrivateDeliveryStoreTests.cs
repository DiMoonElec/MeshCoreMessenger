using System.Buffers.Binary;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class OutgoingMessageStoreTests
{
    private static readonly DateTimeOffset PrivatePcTime = new(2030, 1, 2, 17, 30, 0, TimeSpan.FromHours(3));
    private static PrivateRouteSnapshot Route(bool flood, DateTimeOffset? observed = null) =>
        new(flood ? byte.MaxValue : (byte)0x42, Enumerable.Repeat((byte)0x11, 64).ToArray(), observed ?? PrivatePcTime);

    private static async Task<PreparedOutgoingMessage> BeginCycle(Fixture f, bool flood = true,
        PrivateRepeatMode mode = PrivateRepeatMode.SameTimestampIncrementAttempt)
    {
        var message = await f.Storage.OutgoingMessages.PrepareAsync(f.Request(), CancellationToken);
        await f.Storage.OutgoingMessages.BeginPrivateCycleAsync(new(f.NodeId, message.MessageId, f.SessionId,
            new(mode), Route(flood), PrivatePcTime), CancellationToken);
        return message;
    }

    private static PreparePrivateAttempt PrivatePreparation(Fixture f, Guid message, int previous = 0,
        bool flood = true, DateTimeOffset? pcTime = null) =>
        new(Guid.NewGuid(), f.NodeId, message, f.SessionId, previous, Route(flood), pcTime ?? PrivatePcTime, "Europe/Moscow");

    private static async Task Expire(Fixture f, PreparedPrivateAttempt prepared)
    {
        var at = prepared.Message.Attempt.StartedUtc > prepared.Capture.PreparedPcUtc
            ? prepared.Message.Attempt.StartedUtc : prepared.Capture.PreparedPcUtc;
        var tag = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tag, (uint)prepared.Message.Attempt.AttemptNumber);
        OutgoingAttemptTransition Transition(SendAttemptState from, SendAttemptState to) =>
            new(f.NodeId, prepared.Message.MessageId, prepared.Message.Attempt.Id, f.SessionId, from, to, at.AddSeconds(1));
        Assert.True(await f.Storage.OutgoingMessages.TransitionAsync(Transition(SendAttemptState.Prepared, SendAttemptState.Sending), CancellationToken));
        Assert.True(await f.Storage.OutgoingMessages.TransitionAsync(Transition(SendAttemptState.Sending, SendAttemptState.Accepted) with
        { AckExpectation = AckExpectation.Expected, ExpectedAck = tag, WireTimestamp = prepared.Capture.WireMessage.Timestamp }, CancellationToken));
        Assert.True(await f.Storage.OutgoingMessages.TransitionAsync(Transition(SendAttemptState.Accepted, SendAttemptState.Unconfirmed), CancellationToken));
    }

    [Theory]
    [InlineData(true, PrivateRepeatMode.SameTimestampIncrementAttempt)]
    [InlineData(false, PrivateRepeatMode.SameTimestampIncrementAttempt)]
    [InlineData(true, PrivateRepeatMode.NewTimestampResetAttempt)]
    [InlineData(false, PrivateRepeatMode.NewTimestampResetAttempt)]
    public async Task DurablePrivatePlanPreparesOneBubbleWithCorrectWireIdentitiesAndPcTime(bool flood, PrivateRepeatMode mode)
    {
        await using var f = await Fixture.CreateAsync();
        var original = await BeginCycle(f, flood, mode);
        var count = flood ? 3 : 5;
        var prepared = new List<PreparedPrivateAttempt>();
        for (var index = 0; index < count; index++)
        {
            var request = PrivatePreparation(f, original.MessageId, index, flood || index >= 3);
            var attempt = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(request, CancellationToken);
            prepared.Add(attempt);
            Assert.Equal(index + 1, attempt.Message.Attempt.AttemptNumber);
            Assert.Equal(original.LocalSequence, attempt.Message.LocalSequence);
            Assert.Equal(PrivatePcTime.ToUniversalTime(), attempt.Capture.PreparedPcUtc);
            Assert.Equal(180, attempt.Capture.PcUtcOffsetMinutes);
            Assert.Equal("Europe/Moscow", attempt.Capture.PcTimeZoneId);
            Assert.Equal(request.Route.Path.ToArray(), attempt.Capture.Route.Path.ToArray());
            Assert.Equal(flood || index >= 3 ? 0 : 4, attempt.Capture.Route.Path.Length);
            Assert.Equal(SendAttemptState.Prepared, attempt.Message.Attempt.State);
            Assert.Equal((long)attempt.Capture.WireMessage.Timestamp, attempt.Message.Attempt.WireTimestamp);
            var replay = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(request, CancellationToken);
            Assert.Equal(attempt.Message, replay.Message);
            Assert.Equal(attempt.Capture.WireMessage, replay.Capture.WireMessage);
            await Expire(f, attempt);
        }
        Assert.Single(await f.Storage.History.GetMessagesAsync(f.NodeId, f.ConversationId, null, 20, CancellationToken));
        Assert.Equal(count, (await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, original.MessageId, CancellationToken)).Count);
        var captures = await f.Storage.OutgoingMessages.GetPrivateAttemptCapturesAsync(f.NodeId, original.MessageId, CancellationToken);
        var newEveryTime = mode == PrivateRepeatMode.NewTimestampResetAttempt;
        Assert.Equal(newEveryTime ? new byte[count] : flood ? [0,1,2] : [0,1,2,0,1], captures.Select(c => c.WireAttempt));
        Assert.Equal(newEveryTime ? count : flood ? 1 : 2, captures.Select(c => c.WireMessage.Id).Distinct().Count());
        var baseTimestamp = checked((uint)PrivatePcTime.ToUnixTimeSeconds());
        Assert.Equal(newEveryTime ? Enumerable.Range(0,count).Select(i => baseTimestamp + (uint)i)
            : flood ? [baseTimestamp,baseTimestamp,baseTimestamp]
            : [baseTimestamp,baseTimestamp,baseTimestamp,baseTimestamp+1,baseTimestamp+1], captures.Select(c => c.WireMessage.Timestamp));
        var cycle = (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.NodeId, original.MessageId, CancellationToken))!;
        Assert.Equal(count, cycle.PreparedAttemptCount);
        Assert.Equal(flood ? PrivateDeliveryPhase.Flood : PrivateDeliveryPhase.FallbackFlood, cycle.CurrentPhase);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(
            PrivatePreparation(f, original.MessageId, 5), CancellationToken));
        using var connection = f.Open();
        using var check = connection.CreateCommand(); check.CommandText = "PRAGMA foreign_key_check;";
        Assert.Null(check.ExecuteScalar());
    }

    [Fact]
    public async Task HistoryClearRejectsPrivateCycleBetweenTimedOutAttempts()
    {
        await using var f = await Fixture.CreateAsync();
        var message = await BeginCycle(f);
        var attempt = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(
            PrivatePreparation(f, message.MessageId), CancellationToken);
        await Expire(f, attempt);
        Assert.True((await f.Storage.HistoryClear.GetStatusAsync(f.NodeId, f.ConversationId, CancellationToken)).HasPendingSend);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.HistoryClear.ClearAsync(
            f.NodeId, f.ConversationId, CancellationToken));
        await f.Reopen();
        Assert.False((await f.Storage.HistoryClear.GetStatusAsync(f.NodeId, f.ConversationId, CancellationToken)).HasPendingSend);
        await f.Storage.HistoryClear.ClearAsync(f.NodeId, f.ConversationId, CancellationToken);
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(64, 0, 2)]
    [InlineData(66, 2, 2)]
    [InlineData(130, 2, 3)]
    [InlineData(255, 0, 0)]
    public void PrivateRouteCaptureTrimsPaddingAndOwnsPath(int descriptor, int hops, int hashSize)
    {
        var path = Enumerable.Repeat((byte)0x11, 64).ToArray();
        var route = new PrivateRouteSnapshot((byte)descriptor, path, PrivatePcTime);
        Array.Fill(path, (byte)0x22);
        Assert.Equal(hops, route.HopCount);
        Assert.Equal(hashSize, route.HashSize);
        Assert.Equal(hops * hashSize, route.Path.Length);
        Assert.All(route.Path.ToArray(), value => Assert.Equal((byte)0x11, value));
        Assert.Equal(descriptor == 255 ? PrivateRouteKind.Flood : hops == 0 ? PrivateRouteKind.Direct : PrivateRouteKind.Path, route.Kind);
        Assert.Throws<ArgumentException>(() => new PrivateRouteSnapshot(0xC0, path, PrivatePcTime));
        Assert.Throws<ArgumentException>(() => new PrivateRouteSnapshot(0x42, new byte[3], PrivatePcTime));
    }

    [Fact]
    public async Task DeliveryHistorySchemaRetainsEvidenceAfterConversationClear()
    {
        await using var f = await Fixture.CreateAsync();
        var message = await BeginCycle(f);
        var prepared = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(
            PrivatePreparation(f, message.MessageId), CancellationToken);
        await Expire(f, prepared);
        var delivery = Guid.NewGuid();
        var evidence = Guid.NewGuid();
        // P3 will write these rows atomically with ACK. Here verify the retention contract of migration v5.
        using (var connection = f.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO ContactDeliveryHistory
                    (Id,NodeId,ContactPublicKey,MessageId,SessionId,FirstAckReceivedUtc,PcUtcOffsetMinutes,PcTimeZoneId,WasLate,Attribution)
                VALUES ($delivery,$node,$key,$message,$session,$utc,180,'Europe/Moscow',0,0);
                INSERT INTO ContactDeliveryEvidence (Id,DeliveryId,AckTag,AckReceivedUtc,Attribution)
                VALUES ($evidence,$delivery,X'01000000',$utc,0);
                INSERT INTO ContactDeliveryCandidates
                    (EvidenceId,AttemptNumber,AttemptId,WireMessageOrdinal,WireTimestamp,WireAttempt,Phase,RouteDescriptor,RoutePath)
                VALUES ($evidence,1,$attempt,1,$timestamp,0,1,255,X'');
                """;
            command.Parameters.AddWithValue("$delivery", delivery.ToString("D"));
            command.Parameters.AddWithValue("$evidence", evidence.ToString("D"));
            command.Parameters.AddWithValue("$node", f.NodeId.ToString("D"));
            command.Parameters.AddWithValue("$key", f.Request().Recipient.Identity.ToArray());
            command.Parameters.AddWithValue("$message", message.MessageId.ToString("D"));
            command.Parameters.AddWithValue("$session", f.SessionId.ToString("D"));
            command.Parameters.AddWithValue("$utc", PrivatePcTime.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$attempt", prepared.Message.Attempt.Id.ToString("D"));
            command.Parameters.AddWithValue("$timestamp", (long)prepared.Capture.WireMessage.Timestamp);
            command.ExecuteNonQuery();
        }
        await f.Reopen();
        await f.Storage.HistoryClear.ClearAsync(f.NodeId, f.ConversationId, CancellationToken);
        using var checkConnection = f.Open();
        using var check = checkConnection.CreateCommand();
        check.CommandText = """
            SELECT COUNT(*) FROM ContactDeliveryHistory h
            JOIN ContactDeliveryEvidence e ON e.DeliveryId=h.Id
            JOIN ContactDeliveryCandidates c ON c.EvidenceId=e.Id
            WHERE h.MessageId IS NULL AND c.AttemptId IS NULL
              AND h.PcTimeZoneId='Europe/Moscow' AND c.RouteDescriptor=255;
            """;
        Assert.Equal(1L, check.ExecuteScalar());
        check.CommandText = "PRAGMA foreign_key_check;";
        Assert.Null(check.ExecuteScalar());
    }

    [Fact]
    public async Task PrivatePrepareFailureRollsBackReservationMetadataAndProgressBeforeNotifying()
    {
        await using var f = await Fixture.CreateAsync();
        var message = await BeginCycle(f);
        var request = PrivatePreparation(f, message.MessageId);
        f.Execute("CREATE TRIGGER RejectPrivateCapture BEFORE UPDATE ON SendAttempts WHEN NEW.WireMessageId IS NOT NULL BEGIN SELECT RAISE(ABORT,'injected'); END;");
        var notifications = 0; f.Storage.OutgoingMessages.MessageCommitted += (_, _) => notifications++;
        await Assert.ThrowsAsync<SqliteException>(() => f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(request, CancellationToken));
        Assert.Equal(0, notifications);
        Assert.Empty(await f.Storage.OutgoingMessages.GetPrivateAttemptCapturesAsync(f.NodeId, message.MessageId, CancellationToken));
        Assert.Null(Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, message.MessageId, CancellationToken)).WireTimestamp);
        Assert.Equal(0, (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.NodeId, message.MessageId, CancellationToken))!.PreparedAttemptCount);
        f.Execute("DROP TRIGGER RejectPrivateCapture;");
        var saved = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(request, CancellationToken);
        Assert.Equal((uint)PrivatePcTime.ToUnixTimeSeconds(), saved.Capture.WireMessage.Timestamp);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task PrivatePrepareGuardsOwnershipCounterPhaseAndImmutableTimestamp()
    {
        await using var f = await Fixture.CreateAsync();
        var message = await BeginCycle(f, flood: false);
        var request = PrivatePreparation(f, message.MessageId, flood: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(request with { NodeId = Guid.NewGuid() }, CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(request with { SessionId = Guid.NewGuid() }, CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(request with { Route = Route(true) }, CancellationToken));
        var saved = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(request, CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(request with { PcTimeZoneId = "UTC" }, CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(request with { PreparationId = Guid.NewGuid() }, CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(PrivatePreparation(f, message.MessageId, 1, false), CancellationToken));
        Assert.True(await f.Storage.OutgoingMessages.TransitionAsync(f.Transition(saved.Message, SendAttemptState.Prepared, SendAttemptState.Sending), CancellationToken));
        await Assert.ThrowsAsync<SqliteException>(() => f.Storage.OutgoingMessages.TransitionAsync(
            f.Transition(saved.Message, SendAttemptState.Sending, SendAttemptState.Accepted) with
            { AckExpectation = AckExpectation.Expected, ExpectedAck = new byte[4], WireTimestamp = saved.Capture.WireMessage.Timestamp + 1 }, CancellationToken));
        Assert.Equal((long)saved.Capture.WireMessage.Timestamp, Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, message.MessageId, CancellationToken)).WireTimestamp);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Storage.OutgoingMessages.GetPrivateAttemptCapturesAsync(Guid.NewGuid(), message.MessageId, CancellationToken));
    }

    [Fact]
    public async Task ConcurrentPrivatePreparationsAndIdempotentReplayDoNotAppendOrReserveTwice()
    {
        await using var f = await Fixture.CreateAsync();
        var message = await BeginCycle(f);
        var request = PrivatePreparation(f, message.MessageId);
        var results = await Task.WhenAll(f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(request, CancellationToken),
            f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(request, CancellationToken));
        Assert.Equal(results[0].Message, results[1].Message);
        Assert.Equal(results[0].Capture.WireMessage, results[1].Capture.WireMessage);
        Assert.Single(await f.Storage.OutgoingMessages.GetPrivateAttemptCapturesAsync(f.NodeId, message.MessageId, CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.BeginPrivateCycleAsync(
            new(f.NodeId, message.MessageId, f.SessionId, new(PrivateRepeatMode.NewTimestampResetAttempt), Route(true), PrivatePcTime), CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartStopsPreparedPrivateCyclesAndKeepsTheirWireCapture(bool transmitted)
    {
        await using var f = await Fixture.CreateAsync();
        var message = await BeginCycle(f);
        var prepared = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(PrivatePreparation(f, message.MessageId), CancellationToken);
        if (transmitted) await Expire(f, prepared);
        await f.Reopen();
        var cycle = (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.NodeId, message.MessageId, CancellationToken))!;
        Assert.Equal(PrivateDeliveryState.Unknown, cycle.State);
        Assert.Equal("StartupRecovery", cycle.ErrorCode);
        var retained = Assert.Single(await f.Storage.OutgoingMessages.GetPrivateAttemptCapturesAsync(f.NodeId, message.MessageId, CancellationToken));
        Assert.Equal(prepared.Capture.WireMessage, retained.WireMessage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(
            PrivatePreparation(f, message.MessageId, 1), CancellationToken));
    }

    [Fact]
    public async Task PrivateFloorSurvivesClockRollbackRestartAndHistoryClear()
    {
        await using var f = await Fixture.CreateAsync();
        var first = await BeginCycle(f);
        var prepared = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(PrivatePreparation(f, first.MessageId), CancellationToken);
        await Expire(f, prepared);
        await f.Reopen();
        await f.Storage.HistoryClear.ClearAsync(f.NodeId, f.ConversationId, CancellationToken);
        var second = await BeginCycle(f);
        var next = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(
            PrivatePreparation(f, second.MessageId, pcTime: PrivatePcTime.AddDays(-1)), CancellationToken);
        Assert.Equal(prepared.Capture.WireMessage.Timestamp + 1, next.Capture.WireMessage.Timestamp);
        Assert.Equal(PrivatePcTime.AddDays(-1).ToUniversalTime(), next.Capture.PreparedPcUtc);
    }

    [Fact]
    public async Task LateAckOfEarlierAttemptPreventsPreparingTheNextPrivateTx()
    {
        await using var f = await Fixture.CreateAsync();
        var message = await BeginCycle(f);
        var a = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(PrivatePreparation(f, message.MessageId), CancellationToken);
        await Expire(f, a);
        var b = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(PrivatePreparation(f, message.MessageId, 1), CancellationToken);
        await Expire(f, b);
        Assert.True(await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(new(f.NodeId, f.SessionId, 1, 100, PrivatePcTime.AddSeconds(2)), CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(
            PrivatePreparation(f, message.MessageId, 2), CancellationToken));
        Assert.Equal(2, (await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, message.MessageId, CancellationToken)).Count);
    }

    [Fact]
    public async Task PrivateTimestampOverflowAndCancelledPreparationLeaveNoCapture()
    {
        await using var f = await Fixture.CreateAsync();
        var message = await BeginCycle(f);
        f.Execute($"INSERT INTO PrivateTimestampFloors (NodeId,LastTimestamp) VALUES ('{f.NodeId:D}',4294967295);");
        var request = PrivatePreparation(f, message.MessageId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(request, CancellationToken));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(request, cancelled.Token));
        Assert.Empty(await f.Storage.OutgoingMessages.GetPrivateAttemptCapturesAsync(f.NodeId, message.MessageId, CancellationToken));
    }

    [Fact]
    public async Task V4UpgradePreservesLegacyStateAndSeedsFloorWithoutInventingRouteHistory()
    {
        await using var f = await Fixture.CreateAsync();
        var legacy = await f.Storage.OutgoingMessages.PrepareAsync(f.Request(), CancellationToken);
        await f.Move(legacy, SendAttemptState.Prepared, SendAttemptState.Sending);
        await f.Accept(legacy, AckExpectation.Expected);
        await f.Move(legacy, SendAttemptState.Accepted, SendAttemptState.Unconfirmed);
        await f.Storage.DisposeAsync();
        using (var connection = f.Open())
        {
            DropPrivateDeliverySchema(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM SchemaMigrations WHERE Version=5; PRAGMA user_version=4;";
            command.ExecuteNonQuery();
        }
        f.Storage = await LocalStorage.OpenAsync(f.Paths, CancellationToken);
        Assert.Equal(SendAttemptState.Unconfirmed, Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, legacy.MessageId, CancellationToken)).State);
        Assert.Null(await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.NodeId, legacy.MessageId, CancellationToken));
        Assert.Empty(await f.Storage.OutgoingMessages.GetPrivateAttemptCapturesAsync(f.NodeId, legacy.MessageId, CancellationToken));
        var backup = Assert.Single(Directory.GetFiles(f.Paths.BackupsDirectory, "*.db"));
        using var original = SqliteDatabase.CreateConnection(backup, SqliteOpenMode.ReadOnly); original.Open();
        Assert.Equal(4, SqliteDatabase.GetUserVersion(original));
        var message = await BeginCycle(f);
        var prepared = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(
            PrivatePreparation(f, message.MessageId, pcTime: DateTimeOffset.FromUnixTimeSeconds(1)), CancellationToken);
        Assert.Equal(43u, prepared.Capture.WireMessage.Timestamp);
    }

    [Fact]
    public async Task V5MigrationFailureRollsBackNewTablesAndLeavesV4Backup()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Storage.DisposeAsync();
        using (var connection = f.Open())
        {
            DropPrivateDeliverySchema(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM SchemaMigrations WHERE Version=5; PRAGMA user_version=4; CREATE TABLE PrivateWireMessages (Collision TEXT);";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<DatabaseStorageException>(() => LocalStorage.OpenAsync(f.Paths, CancellationToken));
        using var verify = f.Open();
        Assert.Equal(4, SqliteDatabase.GetUserVersion(verify));
        using var query = verify.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='PrivateTimestampFloors';";
        Assert.Equal(0L, query.ExecuteScalar());
        Assert.Single(Directory.GetFiles(f.Paths.BackupsDirectory, "*.db"));
    }

    // Synthetic downgrade fixtures must remove every newer table/column before exercising an older migration.
    private static void DropPrivateDeliverySchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE ContactDeliveryCandidates; DROP TABLE ContactDeliveryEvidence; DROP TABLE ContactDeliveryHistory;
            DROP TRIGGER TR_SendAttempts_PrivateCaptureImmutable; DROP TRIGGER TR_PrivateWireMessages_Immutable;
            DROP INDEX UX_SendAttempts_PrivatePreparation; DROP INDEX IX_SendAttempts_PrivateAck;
            """;
        command.ExecuteNonQuery();
        foreach (var column in new[] { "PrivatePreparationId", "WireMessageId", "WireAttempt", "RouteDescriptor", "RoutePath", "RouteObservedUtc",
            "PcPreparedUtc", "PcUtcOffsetMinutes", "PcTimeZoneId", "PcSentUtc", "PcSentUtcOffsetMinutes", "PcSentTimeZoneId", "ModeReportedByMsgSent", "AckDeadlineUtc" })
        {
            command.CommandText = $"ALTER TABLE SendAttempts DROP COLUMN {column};";
            command.ExecuteNonQuery();
        }
        command.CommandText = "DROP TABLE PrivateWireMessages; DROP TABLE PrivateDeliveryCycles; DROP TABLE PrivateTimestampFloors;";
        command.ExecuteNonQuery();
    }
}
