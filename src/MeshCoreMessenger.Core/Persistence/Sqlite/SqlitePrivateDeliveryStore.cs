using System.Globalization;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Protocol;
using Microsoft.Data.Sqlite;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed partial class SqliteOutgoingMessageStore
{
    public async Task<PrivateDeliveryCycleSnapshot> BeginPrivateCycleAsync(BeginPrivateDeliveryCycle cycle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ValidatePrivateIds(cycle.NodeId, cycle.MessageId, cycle.SessionId);
        ArgumentNullException.ThrowIfNull(cycle.Policy);
        ArgumentNullException.ThrowIfNull(cycle.InitialRoute);
        var kind = cycle.InitialRoute.Kind;
        var result = await writer.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var message = ReadPrivateMessage(connection, transaction, cycle.NodeId, cycle.MessageId, cycle.SessionId);
            var existing = ReadPrivateCycle(connection, transaction, cycle.NodeId, cycle.MessageId);
            if (existing is not null)
            {
                if (existing.SessionId != cycle.SessionId || existing.Policy != cycle.Policy ||
                    existing.InitialRouteKind != kind || existing.StartedUtc != cycle.StartedUtc.ToUniversalTime())
                    throw new InvalidOperationException("Private cycle already has a different capture.");
                return (Cycle: existing, Conversation: message.ConversationId, Created: false);
            }
            EnsureOwnership(connection, transaction, message);
            var attempts = ReadAttempts(connection, transaction, cycle.NodeId, cycle.MessageId);
            if (attempts.Count != 1 || attempts[0].State != SendAttemptState.Prepared || attempts[0].WireTimestamp is not null)
                throw new InvalidOperationException("Start a private cycle only for a new Prepared message.");
            using var insert = Command(connection, transaction, """
                INSERT INTO PrivateDeliveryCycles
                    (MessageId,NodeId,SessionId,ContactPublicKey,RetryMode,InitialRouteKind,PlannedAttemptCount,StartedUtc)
                VALUES ($message,$node,$session,$key,$mode,$kind,$count,$utc);
                """, ("$message", cycle.MessageId), ("$node", cycle.NodeId), ("$session", cycle.SessionId),
                ("$key", message.Recipient.Identity.ToArray()), ("$mode", (int)cycle.Policy.RetryMode), ("$kind", (int)kind),
                ("$count", kind == PrivateRouteKind.Flood ? 3 : 5), ("$utc", cycle.StartedUtc));
            insert.ExecuteNonQuery();
            var snapshot = ReadPrivateCycle(connection, transaction, cycle.NodeId, cycle.MessageId)!;
            transaction.Commit();
            return (Cycle: snapshot, Conversation: message.ConversationId, Created: true);
        }, cancellationToken).ConfigureAwait(false);
        if (result.Created) Notify(new(cycle.NodeId, result.Conversation, cycle.MessageId, false));
        return result.Cycle;
    }

    public async Task<PreparedPrivateAttempt> PreparePrivateAttemptAsync(PreparePrivateAttempt request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePrivateIds(request.NodeId, request.MessageId, request.SessionId);
        if (request.PreparationId == Guid.Empty || request.ExpectedAttemptNumber is < 0 or > 4)
            throw new ArgumentException("An idempotent preparation ID and preceding attempt 0..4 are required.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.Route);
        if (string.IsNullOrWhiteSpace(request.PcTimeZoneId) || request.PcTimeZoneId.Length > 255)
            throw new ArgumentException("PC timezone ID is required.", nameof(request));
        // Capture caller-owned buffers before admission to the asynchronous writer.
        request = request with { Route = new(request.Route.Descriptor, request.Route.Path, request.Route.ObservedUtc) };
        var result = await writer.ExecuteAsync(connection => PreparePrivate(connection, request), cancellationToken).ConfigureAwait(false);
        if (result.Changed) Notify(new(request.NodeId, result.Conversation, request.MessageId, false));
        return result.Prepared;
    }

    private static (PreparedPrivateAttempt Prepared, Guid Conversation, bool Changed) PreparePrivate(
        SqliteConnection connection, PreparePrivateAttempt request)
    {
        using var transaction = connection.BeginTransaction();
        var message = ReadPrivateMessage(connection, transaction, request.NodeId, request.MessageId, request.SessionId);
        var cycle = ReadPrivateCycle(connection, transaction, request.NodeId, request.MessageId)
            ?? throw new InvalidOperationException("Begin the private delivery cycle before preparing TX.");
        if (cycle.SessionId != request.SessionId) throw new InvalidOperationException("Private cycle session differs.");
        var captures = ReadPrivateCaptures(connection, transaction, request.MessageId);
        using (var preparation = Command(connection, transaction, "SELECT MessageId FROM SendAttempts WHERE PrivatePreparationId=$id;",
            ("$id", request.PreparationId)))
        {
            if (preparation.ExecuteScalar() is { } existingMessage)
            {
                if ((string)existingMessage != request.MessageId.ToString("D"))
                    throw new InvalidOperationException("Preparation ID belongs to another message.");
                var capture = captures.Single(item => item.PreparationId == request.PreparationId);
                var priorAttempts = ReadAttempts(connection, transaction, request.NodeId, request.MessageId);
                var attempt = priorAttempts.Single(item => item.Id == capture.AttemptId);
                if (attempt.AttemptNumber != request.ExpectedAttemptNumber + 1 || attempt.SessionId != request.SessionId ||
                    capture.PreparedPcUtc != request.PreparedPcTime.ToUniversalTime() ||
                    capture.PcUtcOffsetMinutes != (int)request.PreparedPcTime.Offset.TotalMinutes || capture.PcTimeZoneId != request.PcTimeZoneId ||
                    capture.Route.Descriptor != request.Route.Descriptor || capture.Route.ObservedUtc != request.Route.ObservedUtc ||
                    !capture.Route.Path.Span.SequenceEqual(request.Route.Path.Span))
                    throw new InvalidOperationException("Preparation ID already has a different immutable capture.");
                return (new(new(request.MessageId, ReadSequence(connection, transaction, request.MessageId), attempt), capture), message.ConversationId, false);
            }
        }
        EnsureOwnership(connection, transaction, message);
        var attempts = ReadAttempts(connection, transaction, request.NodeId, request.MessageId);
        if (cycle.State is not (PrivateDeliveryState.Prepared or PrivateDeliveryState.Active) ||
            cycle.PreparedAttemptCount != request.ExpectedAttemptNumber || cycle.PreparedAttemptCount >= cycle.PlannedAttemptCount ||
            attempts.Any(item => item.State == SendAttemptState.Delivered))
            throw new InvalidOperationException("Private cycle is finished or its attempt changed.");
        var first = request.ExpectedAttemptNumber == 0;
        var latest = attempts[^1];
        if (latest.SessionId != request.SessionId || (first
                ? attempts.Count != 1 || latest.State != SendAttemptState.Prepared || latest.WireTimestamp is not null
                : latest.AttemptNumber != request.ExpectedAttemptNumber || latest.State != SendAttemptState.Unconfirmed || latest.AckExpectation != AckExpectation.Expected))
            throw new InvalidOperationException("A new attempt requires the previous ACK timeout in this session.");
        var plan = PrivateRetryPlan.Create(cycle.InitialRouteKind == PrivateRouteKind.Flood, cycle.Policy);
        var step = plan.Steps[request.ExpectedAttemptNumber];
        if ((step.Phase != PrivateDeliveryPhase.KnownRoute) != (request.Route.Kind == PrivateRouteKind.Flood))
            throw new InvalidOperationException("Observed route does not match the delivery phase.");
        var previous = captures.LastOrDefault()?.WireMessage;
        var timestamp = step.ResolveTimestamp(previous?.Timestamp, step.RequiresNewTimestamp
            ? ReservePrivateTimestamp(connection, transaction, request.NodeId, request.PreparedPcTime) : null);
        PrivateWireMessageSnapshot wire;
        if (step.RequiresNewTimestamp)
        {
            wire = new(Guid.NewGuid(), request.MessageId, step.WireMessageOrdinal, step.Phase, timestamp);
            using var insert = Command(connection, transaction, """
                INSERT INTO PrivateWireMessages (Id,MessageId,Ordinal,Phase,WireTimestamp) VALUES ($id,$message,$ordinal,$phase,$timestamp);
                """, ("$id", wire.Id), ("$message", request.MessageId), ("$ordinal", wire.Ordinal),
                ("$phase", (int)wire.Phase), ("$timestamp", (long)wire.Timestamp));
            insert.ExecuteNonQuery();
        }
        else wire = previous!;
        var attemptId = first ? latest.Id : Guid.NewGuid();
        if (!first)
        {
            using var insert = Command(connection, transaction, """
                INSERT INTO SendAttempts (Id,MessageId,SessionId,AttemptNumber,State,StartedUtc)
                VALUES ($id,$message,$session,$number,0,$utc);
                """, ("$id", attemptId), ("$message", request.MessageId), ("$session", request.SessionId),
                ("$number", step.AttemptNumber), ("$utc", request.PreparedPcTime));
            insert.ExecuteNonQuery();
        }
        using (var capture = Command(connection, transaction, """
            UPDATE SendAttempts SET PrivatePreparationId=$preparation,WireMessageId=$wire,WireAttempt=$attempt,WireTimestamp=$timestamp,
                RouteDescriptor=$descriptor,RoutePath=$path,RouteObservedUtc=$observed,PcPreparedUtc=$utc,
                PcUtcOffsetMinutes=$offset,PcTimeZoneId=$zone
            WHERE Id=$id AND State=0 AND WireMessageId IS NULL;
            UPDATE PrivateDeliveryCycles SET PreparedAttemptCount=$number,CurrentPhase=$phase,State=1 WHERE MessageId=$message;
            """, ("$preparation", request.PreparationId), ("$wire", wire.Id), ("$attempt", step.WireAttempt), ("$timestamp", (long)timestamp),
            ("$descriptor", request.Route.Descriptor), ("$path", request.Route.Path.ToArray()), ("$observed", request.Route.ObservedUtc),
            ("$utc", request.PreparedPcTime), ("$offset", (int)request.PreparedPcTime.Offset.TotalMinutes), ("$zone", request.PcTimeZoneId),
            ("$id", attemptId), ("$number", step.AttemptNumber), ("$phase", (int)step.Phase), ("$message", request.MessageId))) capture.ExecuteNonQuery();
        var savedAttempt = ReadAttempts(connection, transaction, request.NodeId, request.MessageId).Single(item => item.Id == attemptId);
        var savedCapture = ReadPrivateCaptures(connection, transaction, request.MessageId).Single(item => item.AttemptId == attemptId);
        var prepared = new PreparedPrivateAttempt(new(request.MessageId, ReadSequence(connection, transaction, request.MessageId), savedAttempt), savedCapture);
        transaction.Commit();
        return (prepared, message.ConversationId, true);
    }

    public Task<PrivateDeliveryCycleSnapshot?> GetPrivateCycleAsync(Guid nodeId, Guid messageId, CancellationToken cancellationToken = default) =>
        reader.ExecuteAsync(connection => ReadPrivateCycle(connection, null, nodeId, messageId), cancellationToken);

    public Task<IReadOnlyList<PrivateAttemptCapture>> GetPrivateAttemptCapturesAsync(Guid nodeId, Guid messageId, CancellationToken cancellationToken = default) =>
        reader.ExecuteAsync<IReadOnlyList<PrivateAttemptCapture>>(connection =>
        {
            using var transaction = connection.BeginTransaction();
            _ = ReadAttempts(connection, transaction, nodeId, messageId); // Exact node/message ownership.
            return ReadPrivateCaptures(connection, transaction, messageId);
        }, cancellationToken);

    private static PrepareOutgoingMessage ReadPrivateMessage(SqliteConnection connection, SqliteTransaction transaction,
        Guid nodeId, Guid messageId, Guid sessionId)
    {
        using var query = Command(connection, transaction, """
            SELECT m.ConversationId,m.SessionId,m.Text,m.TransmissionText,c.ContactPublicKey,m.ReceivedUtc
            FROM Messages m JOIN Conversations c ON c.Id=m.ConversationId
            WHERE m.Id=$message AND c.NodeId=$node AND c.Kind=0 AND m.Direction=1 AND m.MessageKind=0;
            """, ("$message", messageId), ("$node", nodeId));
        using var row = query.ExecuteReader();
        if (!row.Read() || row.IsDBNull(1) || row.GetString(1) != sessionId.ToString("D") || row.IsDBNull(3))
            throw new InvalidOperationException("An immutable private send capture in this node/session is required.");
        return new(messageId, nodeId, sessionId, Guid.Parse(row.GetString(0)),
            new(ConversationKind.Contact, (byte[])row.GetValue(4)), row.GetString(2), row.GetString(3), ProtocolLimits.MaxTextBytes, ParseUtc(row.GetString(5)));
    }

    private static PrivateDeliveryCycleSnapshot? ReadPrivateCycle(SqliteConnection connection, SqliteTransaction? transaction, Guid nodeId, Guid messageId)
    {
        using var query = Command(connection, transaction, """
            SELECT SessionId,ContactPublicKey,RetryMode,InitialRouteKind,PlannedAttemptCount,PreparedAttemptCount,State,StartedUtc,ConfirmedUtc,ErrorCode,CurrentPhase
            FROM PrivateDeliveryCycles WHERE MessageId=$message AND NodeId=$node;
            """, ("$message", messageId), ("$node", nodeId));
        using var row = query.ExecuteReader();
        if (!row.Read()) return null;
        return new(nodeId, messageId, row.IsDBNull(0) ? null : Guid.Parse(row.GetString(0)), (byte[])row.GetValue(1),
            new((PrivateRepeatMode)row.GetInt32(2)), (PrivateRouteKind)row.GetInt32(3), row.GetInt32(4), row.GetInt32(5),
            (PrivateDeliveryState)row.GetInt32(6), ParseUtc(row.GetString(7)), row.IsDBNull(8) ? null : ParseUtc(row.GetString(8)), row.IsDBNull(9) ? null : row.GetString(9),
            row.IsDBNull(10) ? null : (PrivateDeliveryPhase)row.GetInt32(10));
    }

    private static List<PrivateAttemptCapture> ReadPrivateCaptures(SqliteConnection connection, SqliteTransaction transaction, Guid messageId)
    {
        using var query = Command(connection, transaction, """
            SELECT a.PrivatePreparationId,a.Id,w.Id,w.MessageId,w.Ordinal,w.Phase,w.WireTimestamp,
                   a.WireAttempt,a.RouteDescriptor,a.RoutePath,a.RouteObservedUtc,a.PcPreparedUtc,a.PcUtcOffsetMinutes,a.PcTimeZoneId
            FROM SendAttempts a JOIN PrivateWireMessages w ON w.Id=a.WireMessageId
            WHERE a.MessageId=$message ORDER BY a.AttemptNumber;
            """, ("$message", messageId));
        using var row = query.ExecuteReader();
        var result = new List<PrivateAttemptCapture>();
        while (row.Read()) result.Add(new(Guid.Parse(row.GetString(0)), Guid.Parse(row.GetString(1)),
            new(Guid.Parse(row.GetString(2)), Guid.Parse(row.GetString(3)), row.GetInt32(4), (PrivateDeliveryPhase)row.GetInt32(5), checked((uint)row.GetInt64(6))),
            checked((byte)row.GetInt32(7)), new(checked((byte)row.GetInt32(8)), (byte[])row.GetValue(9), ParseUtc(row.GetString(10))),
            ParseUtc(row.GetString(11)), row.GetInt32(12), row.GetString(13)));
        return result;
    }

    private static long ReadSequence(SqliteConnection connection, SqliteTransaction transaction, Guid messageId)
    {
        using var query = Command(connection, transaction, "SELECT LocalSequence FROM Messages WHERE Id=$id;", ("$id", messageId));
        return (long)query.ExecuteScalar()!;
    }

    private static uint ReservePrivateTimestamp(SqliteConnection connection, SqliteTransaction transaction, Guid nodeId, DateTimeOffset pcTime)
    {
        using var query = Command(connection, transaction, """
            SELECT MAX(value) FROM (
                SELECT COALESCE((SELECT LastTimestamp FROM PrivateTimestampFloors WHERE NodeId=$node),0) AS value
                UNION ALL
                SELECT COALESCE(MAX(a.WireTimestamp),0) FROM SendAttempts a JOIN Messages m ON m.Id=a.MessageId
                JOIN Conversations c ON c.Id=m.ConversationId
                WHERE c.NodeId=$node AND c.Kind=0 AND m.Direction=1 AND a.WireTimestamp BETWEEN 0 AND 4294967295
            );
            """, ("$node", nodeId));
        var floor = Convert.ToInt64(query.ExecuteScalar(), CultureInfo.InvariantCulture);
        var next = Math.Max(pcTime.ToUnixTimeSeconds(), floor + 1);
        if (next > uint.MaxValue) throw new InvalidOperationException("Private timestamp exceeds the protocol range.");
        ObservePrivateTimestamp(connection, transaction, nodeId, next);
        return (uint)next;
    }

    private static void ObservePrivateTimestamp(SqliteConnection connection, SqliteTransaction transaction, Guid nodeId, long timestamp)
    {
        using var update = Command(connection, transaction, """
            INSERT INTO PrivateTimestampFloors (NodeId,LastTimestamp) VALUES ($node,$timestamp)
            ON CONFLICT(NodeId) DO UPDATE SET LastTimestamp=MAX(LastTimestamp,excluded.LastTimestamp);
            """, ("$node", nodeId), ("$timestamp", timestamp));
        update.ExecuteNonQuery();
    }

    private static void ValidatePrivateIds(Guid node, Guid message, Guid session)
    {
        if (node == Guid.Empty || message == Guid.Empty || session == Guid.Empty) throw new ArgumentException("Exact private node/message/session IDs are required.");
    }
}
