using System.Globalization;
using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed partial class SqliteOutgoingMessageStore(DatabaseWorker writer, DatabaseReader reader) : IOutgoingMessageStore
{
    public event EventHandler<OutgoingMessageCommit>? MessageCommitted;

    public Task<long> GetLatestChannelTimestampAsync(Guid nodeId, CancellationToken cancellationToken = default) =>
        reader.ExecuteAsync(connection =>
        {
            using var query = Command(connection, null, """
                SELECT COALESCE(MAX(a.WireTimestamp),0)
                FROM SendAttempts a JOIN Messages m ON m.Id=a.MessageId
                JOIN Conversations c ON c.Id=m.ConversationId WHERE c.NodeId=$node AND c.Kind=1;
                """, ("$node", nodeId));
            return Convert.ToInt64(query.ExecuteScalar(), CultureInfo.InvariantCulture);
        }, cancellationToken);

    public async Task<PreparedOutgoingMessage> PrepareAsync(PrepareOutgoingMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.Recipient);
        if (message.OperationId == Guid.Empty || message.NodeId == Guid.Empty || message.SessionId == Guid.Empty || message.ConversationId == Guid.Empty)
            throw new ArgumentException("Outgoing ownership IDs must not be empty.", nameof(message));
        ArgumentNullException.ThrowIfNull(message.OriginalText);
        if (message.MaxUtf8Bytes < 0 || message.MaxUtf8Bytes > MeshCoreSharp.Protocol.ProtocolLimits.MaxTextBytes || !TextMessageValidator.Validate(message.TransmissionText, message.MaxUtf8Bytes).IsValid)
            throw new ArgumentException("Transmission text must satisfy the captured UTF-8 budget.", nameof(message));
        var recipient = message.Recipient with { Identity = message.Recipient.Identity.ToArray() };
        if (recipient.Identity.Length != 32 || recipient.Kind is not (ConversationKind.Contact or ConversationKind.Channel))
            throw new ArgumentException("A resolved full recipient identity is required.", nameof(message));
        if (recipient.Kind == ConversationKind.Contact && (recipient.ChannelBindingId is not null || recipient.Slot is not null || recipient.BindingGeneration is not null) ||
            recipient.Kind == ConversationKind.Channel && (recipient.ChannelBindingId is null || recipient.Slot is null || recipient.BindingGeneration is not > 0))
            throw new ArgumentException("Recipient binding is incomplete or inappropriate.", nameof(message));
        message = message with { Recipient = recipient, PreparedUtc = message.PreparedUtc.ToUniversalTime() };
        var outcome = await writer.ExecuteAsync(connection => Prepare(connection, message), cancellationToken).ConfigureAwait(false);
        if (outcome.Inserted) Notify(new(message.NodeId, message.ConversationId, outcome.Message.MessageId, true));
        return outcome.Message;
    }

    private static (PreparedOutgoingMessage Message, bool Inserted) Prepare(SqliteConnection connection, PrepareOutgoingMessage message)
    {
        using var transaction = connection.BeginTransaction();
        // Immutable operation comparison also works after session/binding retirement.
        using (var existing = Command(connection, transaction, """
            SELECT m.LocalSequence, m.ConversationId, m.SessionId, m.Text, m.TransmissionText,
                   m.ChannelBindingId, c.NodeId, c.Kind, c.ContactPublicKey, ch.KeyFingerprint,
                   b.Slot, b.Generation, m.Direction
            FROM Messages m JOIN Conversations c ON c.Id=m.ConversationId
            LEFT JOIN Channels ch ON ch.Id=c.ChannelId
            LEFT JOIN ChannelBindings b ON b.Id=m.ChannelBindingId WHERE m.Id=$id;
            """, ("$id", message.OperationId)))
        using (var row = existing.ExecuteReader())
        {
            if (row.Read())
            {
                var identityColumn = message.Recipient.Kind == ConversationKind.Contact ? 8 : 9;
                var identity = row.IsDBNull(identityColumn) ? [] : (byte[])row.GetValue(identityColumn);
                if (row.GetInt32(12) != (int)MessageDirection.Outgoing || row.GetInt32(7) != (int)message.Recipient.Kind ||
                    row.GetString(1) != message.ConversationId.ToString("D") || row.IsDBNull(2) || row.GetString(2) != message.SessionId.ToString("D") ||
                    row.GetString(3) != message.OriginalText || row.IsDBNull(4) || row.GetString(4) != message.TransmissionText ||
                    row.GetString(6) != message.NodeId.ToString("D") ||
                    !identity.AsSpan().SequenceEqual(message.Recipient.Identity.Span) ||
                    (message.Recipient.Kind == ConversationKind.Channel && (row.GetString(5) != message.Recipient.ChannelBindingId!.Value.ToString("D") ||
                        row.GetInt32(10) != message.Recipient.Slot || row.GetInt32(11) != message.Recipient.BindingGeneration)))
                    throw new InvalidOperationException("Operation ID already belongs to different content or ownership.");
                var sequence = row.GetInt64(0);
                row.Close();
                var attempts = ReadAttempts(connection, transaction, message.NodeId, message.OperationId);
                transaction.Commit();
                return (new(message.OperationId, sequence, attempts[0]), false);
            }
        }
        EnsureOwnership(connection, transaction, message);
        using (var insert = Command(connection, transaction, """
            INSERT INTO Messages (Id, ConversationId, SessionId, Direction, MessageKind, Text, TransmissionText,
                                  ReceivedUtc, OriginalChannelSlot, ChannelBindingId, ResolutionState)
            VALUES ($id,$conversation,$session,1,0,$original,$transmission,$utc,$slot,$binding,1);
            """, ("$id", message.OperationId), ("$conversation", message.ConversationId), ("$session", message.SessionId),
            ("$original", message.OriginalText), ("$transmission", message.TransmissionText), ("$utc", message.PreparedUtc),
            ("$slot", message.Recipient.Slot), ("$binding", message.Recipient.ChannelBindingId))) insert.ExecuteNonQuery();
        long sequenceId;
        using (var sequence = Command(connection, transaction, "SELECT last_insert_rowid();")) sequenceId = (long)sequence.ExecuteScalar()!;
        var attemptId = Guid.NewGuid();
        using (var insert = Command(connection, transaction, """
            INSERT INTO SendAttempts (Id, MessageId, SessionId, AttemptNumber, State, StartedUtc)
            VALUES ($id,$message,$session,1,0,$utc);
            UPDATE Conversations SET UpdatedUtc=$utc WHERE Id=$conversation;
            """, ("$id", attemptId), ("$message", message.OperationId), ("$session", message.SessionId),
            ("$utc", message.PreparedUtc), ("$conversation", message.ConversationId))) insert.ExecuteNonQuery();
        var attempt = ReadAttempts(connection, transaction, message.NodeId, message.OperationId)[0];
        transaction.Commit();
        return (new(message.OperationId, sequenceId, attempt), true);
    }

    private static void EnsureOwnership(SqliteConnection connection, SqliteTransaction transaction, PrepareOutgoingMessage message)
    {
        using var command = Command(connection, transaction, """
            SELECT c.Kind,c.ContactPublicKey,ch.KeyFingerprint FROM Conversations c
            JOIN Sessions s ON s.Id=$session AND s.NodeId=c.NodeId AND s.EndedUtc IS NULL
            LEFT JOIN Channels ch ON ch.Id=c.ChannelId
            WHERE c.Id=$conversation AND c.NodeId=$node AND c.IsArchived=0;
            """, ("$session", message.SessionId), ("$conversation", message.ConversationId), ("$node", message.NodeId));
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetInt32(0) != (int)message.Recipient.Kind ||
            !((byte[])row.GetValue(message.Recipient.Kind == ConversationKind.Contact ? 1 : 2)).AsSpan().SequenceEqual(message.Recipient.Identity.Span))
            throw new InvalidOperationException("Conversation, recipient and active session must belong to the captured node.");
        row.Close();
        if (message.Recipient.Kind == ConversationKind.Contact)
        {
            using var contact = Command(connection, transaction, """
                SELECT 1 FROM Contacts c WHERE c.NodeId=$node AND c.PublicKey=$key AND c.PresentOnNode=1 AND c.ContactType=1
                  AND (SELECT COUNT(*) FROM Contacts other WHERE other.NodeId=c.NodeId
                       AND other.PresentOnNode=1 AND other.PublicKeyPrefix=c.PublicKeyPrefix)=1;
                """, ("$node", message.NodeId), ("$key", message.Recipient.Identity.ToArray()));
            if (contact.ExecuteScalar() is null) throw new InvalidOperationException("Contact is absent, not a companion, or has an ambiguous wire prefix.");
        }
        if (message.Recipient.Kind == ConversationKind.Channel)
        {
            using var binding = Command(connection, transaction, """
                SELECT 1 FROM ChannelBindings b JOIN Conversations c ON c.ChannelId=b.ChannelId
                WHERE c.Id=$conversation AND b.Id=$binding AND b.NodeId=$node AND b.Slot=$slot
                  AND b.Generation=$generation AND b.UnboundUtc IS NULL;
                """, ("$conversation", message.ConversationId), ("$binding", message.Recipient.ChannelBindingId),
                ("$node", message.NodeId), ("$slot", message.Recipient.Slot), ("$generation", message.Recipient.BindingGeneration));
            if (binding.ExecuteScalar() is null) throw new InvalidOperationException("Captured channel binding is no longer active.");
        }
    }

    public async Task<PreparedOutgoingMessage> PrepareChannelRepeatAsync(PrepareChannelRepeat repeat, CancellationToken cancellationToken = default)
    {
        var recipient = repeat.Recipient with { Identity = repeat.Recipient.Identity.ToArray() };
        if (recipient.Kind != ConversationKind.Channel || recipient.Identity.Length != 32 || repeat.ExpectedAttemptNumber < 1)
            throw new ArgumentException("A resolved channel and previous attempt are required.", nameof(repeat));
        var prepared = await writer.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var query = Command(connection, transaction, """
                SELECT m.LocalSequence,m.ConversationId,m.Text,m.TransmissionText,ch.KeyFingerprint
                FROM Messages m JOIN Conversations c ON c.Id=m.ConversationId JOIN Channels ch ON ch.Id=c.ChannelId
                WHERE m.Id=$message AND c.NodeId=$node AND c.Kind=1 AND m.Direction=1 AND m.MessageKind=0;
                """, ("$message", repeat.MessageId), ("$node", repeat.NodeId));
            using var row = query.ExecuteReader();
            if (!row.Read() || row.IsDBNull(3) || !((byte[])row.GetValue(4)).AsSpan().SequenceEqual(recipient.Identity.Span))
                throw new InvalidOperationException("Only a captured outgoing message in this channel can be repeated.");
            var sequence = row.GetInt64(0); var conversation = Guid.Parse(row.GetString(1));
            var original = row.GetString(2); var text = row.GetString(3); row.Close();
            var attempts = ReadAttempts(connection, transaction, repeat.NodeId, repeat.MessageId);
            var latest = attempts[^1];
            if (latest.AttemptNumber != repeat.ExpectedAttemptNumber || latest.State == SendAttemptState.Sending ||
                latest.State == SendAttemptState.Accepted && latest.AckExpectation != AckExpectation.NotExpected)
                throw new InvalidOperationException("The message attempt has changed or is still sending.");
            EnsureOwnership(connection, transaction, new(repeat.MessageId, repeat.NodeId, repeat.SessionId,
                conversation, recipient, original, text, MeshCoreSharp.Protocol.ProtocolLimits.MaxTextBytes, repeat.PreparedUtc));
            var id = Guid.NewGuid();
            using var insert = Command(connection, transaction, """
                INSERT INTO SendAttempts (Id,MessageId,SessionId,AttemptNumber,State,StartedUtc)
                VALUES ($id,$message,$session,$number,0,$utc);
                """, ("$id", id), ("$message", repeat.MessageId), ("$session", repeat.SessionId),
                ("$number", checked(latest.AttemptNumber + 1)), ("$utc", repeat.PreparedUtc.ToUniversalTime()));
            insert.ExecuteNonQuery();
            var attempt = ReadAttempts(connection, transaction, repeat.NodeId, repeat.MessageId)[^1];
            transaction.Commit();
            return (Prepared: new PreparedOutgoingMessage(repeat.MessageId, sequence, attempt), Conversation: conversation);
        }, cancellationToken).ConfigureAwait(false);
        Notify(new(repeat.NodeId, prepared.Conversation, repeat.MessageId, false));
        return prepared.Prepared;
    }

    public async Task<bool> TransitionAsync(OutgoingAttemptTransition transition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (!Enum.IsDefined(transition.State) || !Enum.IsDefined(transition.ExpectedState) || !Enum.IsDefined(transition.AckExpectation) ||
            transition.NodeId == Guid.Empty || transition.MessageId == Guid.Empty || transition.AttemptId == Guid.Empty || transition.SessionId == Guid.Empty ||
            transition.RoundTripMilliseconds < 0) throw new ArgumentException("Invalid transition.", nameof(transition));
        var copied = transition with { ExpectedAck = transition.ExpectedAck?.ToArray(), AtUtc = transition.AtUtc.ToUniversalTime() };
        var commit = await writer.ExecuteAsync(connection => Transition(connection, copied), cancellationToken).ConfigureAwait(false);
        if (commit is null) return false;
        Notify(commit);
        return true;
    }

    private static OutgoingMessageCommit? Transition(SqliteConnection connection, OutgoingAttemptTransition transition)
    {
        using var transaction = connection.BeginTransaction();
        var attempts = ReadAttempts(connection, transaction, transition.NodeId, transition.MessageId);
        var attempt = attempts.SingleOrDefault(item => item.Id == transition.AttemptId);
        if (attempt is null || attempt.SessionId != transition.SessionId) throw new InvalidOperationException("Attempt ownership mismatch.");
        if (attempt.State != transition.ExpectedState) return null;
        var allowed = SendAttemptTransitions.Allows(attempt.State, attempt.AckExpectation, transition.State);
        if (!allowed) throw new InvalidOperationException("Attempt transition would violate delivery ordering or change a terminal state.");
        if (transition.AtUtc < attempt.StartedUtc || transition.AtUtc < attempt.AcceptedUtc)
            throw new ArgumentException("Transition precedes recorded attempt time.", nameof(transition));
        if (transition.State == SendAttemptState.Accepted &&
            (transition.AckExpectation == AckExpectation.LegacyUnknown || transition.AckExpectation == AckExpectation.Expected && (transition.ExpectedAck is null || transition.ExpectedAck.Value.Length != sizeof(uint))))
            throw new ArgumentException("Accepted requires explicit ACK expectation and a tag when ACK is expected.", nameof(transition));
        using var scope = Command(connection, transaction, "SELECT ConversationId FROM Messages WHERE Id=$message;", ("$message", transition.MessageId));
        var conversationId = Guid.Parse((string)scope.ExecuteScalar()!);
        if (transition.State == SendAttemptState.Accepted && transition.AckExpectation == AckExpectation.Expected)
        {
            using var kind = Command(connection, transaction, "SELECT Kind FROM Conversations WHERE Id=$id;", ("$id", conversationId));
            if (Convert.ToInt32(kind.ExecuteScalar()) != (int)ConversationKind.Contact) throw new InvalidOperationException("Channel sends cannot await ACK.");
        }
        if (transition.State is SendAttemptState.Sending or SendAttemptState.Accepted && transition.WireTimestamp is { } timestamp)
        {
            using var kind = Command(connection, transaction, "SELECT Kind FROM Conversations WHERE Id=$id;", ("$id", conversationId));
            if (Convert.ToInt32(kind.ExecuteScalar()) == (int)ConversationKind.Contact)
            {
                if (timestamp is < 0 or > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(transition));
                ObservePrivateTimestamp(connection, transaction, transition.NodeId, timestamp);
            }
        }
        if (transition.State == SendAttemptState.Delivered)
        {
            if (attempt.ExpectedAck is not { } expectedAck || expectedAck.Length != sizeof(uint))
                throw new InvalidOperationException("Delivery requires persisted ACK evidence.");
            var tag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(expectedAck.Span);
            var outcome = CommitAcknowledgement(connection, transaction, new(transition.NodeId, transition.SessionId,
                tag, (uint)(transition.RoundTripMilliseconds ?? 0), transition.AtUtc));
            transaction.Commit();
            return outcome.Commit;
        }
        var terminal = transition.State is SendAttemptState.Delivered or SendAttemptState.Unconfirmed or SendAttemptState.Failed or SendAttemptState.Unknown ||
            transition.State == SendAttemptState.Accepted && transition.AckExpectation == AckExpectation.NotExpected;
        using var update = Command(connection, transaction, """
            UPDATE SendAttempts SET State=$state,
                AcceptedUtc=CASE WHEN $state=1 THEN $utc ELSE AcceptedUtc END,
                CompletedUtc=CASE WHEN $terminal THEN $utc ELSE CompletedUtc END,
                AckExpectation=CASE WHEN $state=1 THEN $expectation ELSE AckExpectation END,
                WireTimestamp=CASE WHEN $state IN (1,6) THEN COALESCE($timestamp,WireTimestamp) ELSE WireTimestamp END,
                ExpectedAck=CASE WHEN $state=1 THEN $ack ELSE ExpectedAck END,
                RoundTripMilliseconds=COALESCE($rtt,RoundTripMilliseconds), ErrorCode=$error
            WHERE Id=$id AND State=$expected;
            """, ("$state", (int)transition.State), ("$utc", transition.AtUtc), ("$terminal", terminal),
            ("$expectation", (int)transition.AckExpectation), ("$timestamp", transition.WireTimestamp),
            ("$ack", transition.ExpectedAck?.ToArray()), ("$rtt", transition.RoundTripMilliseconds), ("$error", transition.ErrorCode),
            ("$id", transition.AttemptId), ("$expected", (int)transition.ExpectedState));
        update.ExecuteNonQuery();
        UpdatePrivateCycleOutcome(connection, transaction, transition);
        transaction.Commit();
        return new(transition.NodeId, conversationId, transition.MessageId, false);
    }

    public async Task<bool> ConfirmAcknowledgementAsync(OutgoingAcknowledgement acknowledgement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (acknowledgement.NodeId == Guid.Empty || acknowledgement.SessionId == Guid.Empty || acknowledgement.Tag == 0)
            throw new ArgumentException("ACK requires an identified node, session and nonzero tag.", nameof(acknowledgement));
        if (acknowledgement.PcUtcOffsetMinutes is < -840 or > 840 || string.IsNullOrWhiteSpace(acknowledgement.PcTimeZoneId))
            throw new ArgumentException("ACK requires PC offset and timezone.", nameof(acknowledgement));
        var outcome = await writer.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var result = CommitAcknowledgement(connection, transaction, acknowledgement);
            transaction.Commit();
            return result;
        }, cancellationToken).ConfigureAwait(false);
        if (outcome.Commit is { } commit) Notify(commit);
        return outcome.Handled;
    }

    public Task<StoredOutgoingMessage> GetAsync(Guid nodeId, Guid messageId, CancellationToken cancellationToken = default) =>
        reader.ExecuteAsync(connection =>
        {
            using var command = Command(connection, null, """
                SELECT m.ConversationId,m.SessionId,c.Kind,c.ContactPublicKey,ch.KeyFingerprint,
                       b.Id,b.Slot,b.Generation,m.Text,m.TransmissionText
                FROM Messages m JOIN Conversations c ON c.Id=m.ConversationId
                LEFT JOIN Channels ch ON ch.Id=c.ChannelId
                LEFT JOIN ChannelBindings b ON b.Id=m.ChannelBindingId
                WHERE m.Id=$message AND c.NodeId=$node AND m.Direction=1;
                """, ("$message", messageId), ("$node", nodeId));
            using var row = command.ExecuteReader();
            if (!row.Read()) throw new KeyNotFoundException("Outgoing message does not belong to the node.");
            var kind = (ConversationKind)row.GetInt32(2);
            if (kind is not (ConversationKind.Contact or ConversationKind.Channel) || row.IsDBNull(9))
                throw new InvalidOperationException("Legacy outgoing message has no complete transmission capture.");
            var recipient = kind == ConversationKind.Contact
                ? new OutgoingRecipient(kind, (byte[])row.GetValue(3))
                : new OutgoingRecipient(kind, (byte[])row.GetValue(4), Guid.Parse(row.GetString(5)), checked((byte)row.GetInt32(6)), row.GetInt32(7));
            return new StoredOutgoingMessage(messageId, nodeId, Guid.Parse(row.GetString(0)),
                row.IsDBNull(1) ? null : Guid.Parse(row.GetString(1)), recipient, row.GetString(8), row.GetString(9));
        }, cancellationToken);

    public Task<IReadOnlyList<OutgoingAttemptSnapshot>> GetAttemptsAsync(Guid nodeId, Guid messageId, CancellationToken cancellationToken = default) =>
        reader.ExecuteAsync<IReadOnlyList<OutgoingAttemptSnapshot>>(connection => ReadAttempts(connection, null, nodeId, messageId), cancellationToken);

    internal static List<OutgoingAttemptSnapshot> ReadAttempts(SqliteConnection connection, SqliteTransaction? transaction, Guid nodeId, Guid messageId)
    {
        using var ownership = Command(connection, transaction, """
            SELECT 1 FROM Messages m JOIN Conversations c ON c.Id=m.ConversationId
            WHERE m.Id=$message AND c.NodeId=$node AND m.Direction=1;
            """, ("$message", messageId), ("$node", nodeId));
        if (ownership.ExecuteScalar() is null) throw new KeyNotFoundException("Outgoing message does not belong to the node.");
        using var command = Command(connection, transaction, """
            SELECT Id,MessageId,SessionId,AttemptNumber,State,AckExpectation,StartedUtc,AcceptedUtc,CompletedUtc,
                   WireTimestamp,ExpectedAck,RoundTripMilliseconds,ErrorCode
            FROM SendAttempts WHERE MessageId=$message ORDER BY AttemptNumber;
            """, ("$message", messageId));
        using var row = command.ExecuteReader();
        var attempts = new List<OutgoingAttemptSnapshot>();
        while (row.Read()) attempts.Add(ReadAttempt(row));
        return attempts;
    }

    internal static OutgoingAttemptSnapshot ReadAttempt(SqliteDataReader row, int offset = 0) => new(
        Guid.Parse(row.GetString(offset)), Guid.Parse(row.GetString(offset + 1)), row.IsDBNull(offset + 2) ? null : Guid.Parse(row.GetString(offset + 2)),
        row.GetInt32(offset + 3), (SendAttemptState)row.GetInt32(offset + 4), (AckExpectation)row.GetInt32(offset + 5),
        ParseUtc(row.GetString(offset + 6)), row.IsDBNull(offset + 7) ? null : ParseUtc(row.GetString(offset + 7)),
        row.IsDBNull(offset + 8) ? null : ParseUtc(row.GetString(offset + 8)), row.IsDBNull(offset + 9) ? null : row.GetInt64(offset + 9),
        row.IsDBNull(offset + 10) ? null : new ReadOnlyMemory<byte>((byte[])row.GetValue(offset + 10)), row.IsDBNull(offset + 11) ? null : row.GetInt32(offset + 11),
        row.IsDBNull(offset + 12) ? null : row.GetString(offset + 12));

    internal static void Recover(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = Command(connection, transaction, """
            UPDATE SendAttempts SET State=5, CompletedUtc=MAX(StartedUtc,COALESCE(AcceptedUtc,StartedUtc),$utc), ErrorCode='StartupRecovery'
            WHERE State=6 OR (State=1 AND (AckExpectation=2 OR (AckExpectation=0 AND MessageId IN (
                SELECT m.Id FROM Messages m JOIN Conversations c ON c.Id=m.ConversationId WHERE c.Kind<>1))));
            UPDATE SendAttempts SET AckExpectation=1, CompletedUtc=COALESCE(CompletedUtc,AcceptedUtc,StartedUtc)
            WHERE State=1 AND AckExpectation=0 AND MessageId IN (
                SELECT m.Id FROM Messages m JOIN Conversations c ON c.Id=m.ConversationId WHERE c.Kind=1);
            """, ("$utc", DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
        using var cycles = Command(connection, transaction, """
            UPDATE PrivateDeliveryCycles SET State=5, ErrorCode='StartupRecovery'
            WHERE State IN (0,1);
            """);
        cycles.ExecuteNonQuery();
        transaction.Commit();
    }

    private void Notify(OutgoingMessageCommit commit)
    {
        if (MessageCommitted is not { } handlers) return;
        foreach (EventHandler<OutgoingMessageCommit> handler in handlers.GetInvocationList())
        {
            try { handler(this, commit); }
            catch { /* A subscriber cannot undo a committed message or interrupt other subscribers. */ }
        }
    }

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value switch
        {
            null => DBNull.Value,
            Guid guid => guid.ToString("D"),
            DateTimeOffset utc => utc.ToUniversalTime().ToString("O"),
            _ => value,
        });
        return command;
    }
}
