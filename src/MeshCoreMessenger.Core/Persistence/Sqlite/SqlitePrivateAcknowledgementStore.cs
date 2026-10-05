using System.Buffers.Binary;
using MeshCoreMessenger.Core.Domain;
using Microsoft.Data.Sqlite;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed partial class SqliteOutgoingMessageStore
{
    private static (bool Handled, OutgoingMessageCommit? Commit) CommitAcknowledgement(
        SqliteConnection connection, SqliteTransaction transaction, OutgoingAcknowledgement ack)
    {
        if (ack.Tag == 0) throw new ArgumentException("Zero is not an expected ACK tag.", nameof(ack));
        var tag = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(tag, ack.Tag);
        var matches = new List<(Guid Attempt, Guid Message, Guid Conversation, SendAttemptState State)>();
        using (var query = Command(connection, transaction, """
            SELECT a.Id,m.Id,m.ConversationId,a.State FROM SendAttempts a
            JOIN Messages m ON m.Id=a.MessageId JOIN Conversations c ON c.Id=m.ConversationId
            WHERE c.NodeId=$node AND c.Kind=0 AND m.Direction=1 AND a.SessionId=$session
              AND a.AckExpectation=2 AND a.ExpectedAck=$tag;
            """, ("$node", ack.NodeId), ("$session", ack.SessionId), ("$tag", tag)))
        using (var rows = query.ExecuteReader())
            while (rows.Read()) matches.Add((Guid.Parse(rows.GetString(0)), Guid.Parse(rows.GetString(1)),
                Guid.Parse(rows.GetString(2)), (SendAttemptState)rows.GetInt32(3)));
        using (var sending = Command(connection, transaction, """
            SELECT 1 FROM SendAttempts a JOIN Messages m ON m.Id=a.MessageId
            JOIN Conversations c ON c.Id=m.ConversationId
            WHERE c.NodeId=$node AND c.Kind=0 AND a.SessionId=$session AND a.State=6 LIMIT 1;
            """, ("$node", ack.NodeId), ("$session", ack.SessionId)))
        {
            // Wait for all in-flight MSG_SENT metadata, even when a previous tag already matches.
            // Otherwise a collision with the currently unknown tag could falsely confirm the previous message.
            if (sending.ExecuteScalar() is not null) return (false, null);
        }
        // Writer order determines admission. Wall clock can move backwards during an in-flight command.
        if (matches.Count == 0) return (true, null);
        // Completed attempts also participate: a collision never chooses the latest/pending message.
        if (matches.Select(item => item.Message).Distinct().Count() != 1 ||
            !matches.Any(item => item.State is SendAttemptState.Accepted or SendAttemptState.Unconfirmed or SendAttemptState.Unknown or SendAttemptState.Delivered))
            return (true, null);
        var message = matches[0].Message;
        var cycle = ReadPrivateCycle(connection, transaction, ack.NodeId, message);
        var attempts = ReadAttempts(connection, transaction, ack.NodeId, message);
        var candidates = attempts.Where(item => matches.Any(match => match.Attempt == item.Id)).ToArray();
        // Library collision faults may occur before the second MSG_SENT is returned to Core.
        // Its tag is then unavailable here: do not reinterpret a protocol fault as a unique success.
        if (candidates.Any(item => item.State == SendAttemptState.Unknown &&
            item.ErrorCode == nameof(MeshCoreSharp.Exceptions.MeshCoreProtocolException))) return (true, null);
        var captures = ReadPrivateCaptures(connection, transaction, message);
        var attribution = candidates.Length > 1 ? DeliveryAttribution.MultipleCandidates
            : captures.Any(item => item.AttemptId == candidates[0].Id) ? DeliveryAttribution.SingleAttempt : DeliveryAttribution.Unknown;
        var changed = false;
        // An ambiguous attempt is left untouched; the authoritative cycle is confirmed instead.
        if (candidates.Length == 1 && candidates[0].State is SendAttemptState.Accepted or SendAttemptState.Unconfirmed or SendAttemptState.Unknown)
        {
            var candidate = candidates[0];
            var completed = new[] { ack.ReceivedUtc.ToUniversalTime(), candidate.StartedUtc,
                candidate.AcceptedUtc ?? candidate.StartedUtc, candidate.CompletedUtc ?? candidate.StartedUtc }.Max();
            using var update = Command(connection, transaction, """
                UPDATE SendAttempts SET State=2,CompletedUtc=$utc,RoundTripMilliseconds=$rtt,ErrorCode=NULL WHERE Id=$id;
                """, ("$id", candidate.Id), ("$utc", completed),
                ("$rtt", ack.RoundTripMilliseconds <= int.MaxValue ? (int?)ack.RoundTripMilliseconds : null));
            changed = update.ExecuteNonQuery() != 0;
        }
        if (cycle is not null && cycle.State != PrivateDeliveryState.Delivered)
        {
            using var update = Command(connection, transaction, """
                UPDATE PrivateDeliveryCycles SET State=2,ConfirmedUtc=$utc,ErrorCode=NULL WHERE MessageId=$message AND State<>2;
                """, ("$message", message), ("$utc", ack.ReceivedUtc));
            changed |= update.ExecuteNonQuery() != 0;
        }
        using var contact = Command(connection, transaction,
            "SELECT ContactPublicKey FROM Conversations WHERE Id=$id;", ("$id", matches[0].Conversation));
        var key = (byte[])contact.ExecuteScalar()!;
        var deliveryId = Guid.NewGuid();
        using (var insert = Command(connection, transaction, """
            INSERT INTO ContactDeliveryHistory
                (Id,NodeId,ContactPublicKey,MessageId,SessionId,FirstAckReceivedUtc,PcUtcOffsetMinutes,PcTimeZoneId,WasLate,Attribution)
            VALUES ($id,$node,$key,$message,$session,$utc,$offset,$zone,$late,$attribution)
            ON CONFLICT(MessageId) DO NOTHING;
            """, ("$id", deliveryId), ("$node", ack.NodeId), ("$key", key), ("$message", message),
            ("$session", ack.SessionId), ("$utc", ack.ReceivedUtc), ("$offset", ack.PcUtcOffsetMinutes),
            ("$zone", ack.PcTimeZoneId), ("$late", cycle is null
                ? candidates.All(item => item.State == SendAttemptState.Unconfirmed)
                : cycle.State is PrivateDeliveryState.Unconfirmed or PrivateDeliveryState.Failed or PrivateDeliveryState.Unknown),
            ("$attribution", (int)attribution))) changed |= insert.ExecuteNonQuery() != 0;
        using (var lookup = Command(connection, transaction, "SELECT Id FROM ContactDeliveryHistory WHERE MessageId=$message;", ("$message", message)))
            deliveryId = Guid.Parse((string)lookup.ExecuteScalar()!);
        var evidenceId = Guid.NewGuid();
        using (var insert = Command(connection, transaction, """
            INSERT INTO ContactDeliveryEvidence (Id,DeliveryId,AckTag,AckReceivedUtc,RoundTripMilliseconds,Attribution)
            VALUES ($id,$delivery,$tag,$utc,$rtt,$attribution) ON CONFLICT(DeliveryId,AckTag) DO NOTHING;
            """, ("$id", evidenceId), ("$delivery", deliveryId), ("$tag", tag), ("$utc", ack.ReceivedUtc),
            ("$rtt", (long)ack.RoundTripMilliseconds), ("$attribution", (int)attribution)))
        {
            if (insert.ExecuteNonQuery() != 0)
            {
                changed = true;
                foreach (var capture in captures.Where(item => candidates.Any(candidate => candidate.Id == item.AttemptId)))
                {
                    var attempt = candidates.Single(item => item.Id == capture.AttemptId);
                    using var candidate = Command(connection, transaction, """
                        INSERT INTO ContactDeliveryCandidates
                            (EvidenceId,AttemptNumber,AttemptId,WireMessageOrdinal,WireTimestamp,WireAttempt,Phase,
                             SentUtc,PcUtcOffsetMinutes,PcTimeZoneId,RouteDescriptor,RoutePath,RouteObservedUtc,ModeReportedByMsgSent)
                        SELECT $evidence,AttemptNumber,Id,$ordinal,$timestamp,$attempt,$phase,
                               PcSentUtc,PcSentUtcOffsetMinutes,PcSentTimeZoneId,RouteDescriptor,RoutePath,RouteObservedUtc,ModeReportedByMsgSent
                        FROM SendAttempts WHERE Id=$id;
                        """, ("$evidence", evidenceId), ("$ordinal", capture.WireMessage.Ordinal),
                        ("$timestamp", (long)capture.WireMessage.Timestamp), ("$attempt", capture.WireAttempt),
                        ("$phase", (int)capture.WireMessage.Phase), ("$id", attempt.Id));
                    candidate.ExecuteNonQuery();
                }
            }
        }
        return (true, changed ? new(ack.NodeId, matches[0].Conversation, message, false) : null);
    }

    private static void UpdatePrivateCycleOutcome(SqliteConnection connection, SqliteTransaction transaction,
        OutgoingAttemptTransition transition)
    {
        var outcome = transition.State switch
        {
            SendAttemptState.Failed => PrivateDeliveryState.Failed,
            SendAttemptState.Unknown => PrivateDeliveryState.Unknown,
            SendAttemptState.Unconfirmed => PrivateDeliveryState.Unconfirmed,
            SendAttemptState.Accepted when transition.AckExpectation == AckExpectation.NotExpected => PrivateDeliveryState.Failed,
            _ => (PrivateDeliveryState?)null,
        };
        if (outcome is null) return;
        using var update = Command(connection, transaction, """
            UPDATE PrivateDeliveryCycles SET State=$state,ErrorCode=$error WHERE MessageId=$message AND State<>2
              AND ($state<>3 OR PreparedAttemptCount=PlannedAttemptCount)
              AND PreparedAttemptCount=(SELECT AttemptNumber FROM SendAttempts WHERE Id=$attempt);
            """, ("$state", (int)outcome), ("$error", transition.ErrorCode),
            ("$message", transition.MessageId), ("$attempt", transition.AttemptId));
        update.ExecuteNonQuery();
    }
}
