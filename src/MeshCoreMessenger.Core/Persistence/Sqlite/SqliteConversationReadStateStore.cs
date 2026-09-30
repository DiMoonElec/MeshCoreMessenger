using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed class SqliteConversationReadStateStore(
    DatabaseWorker writer,
    DatabaseReader reader) : IConversationReadStateStore
{
    public Task<ConversationReadState> GetAsync(
        Guid nodeId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(nodeId, conversationId);
        return reader.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var state = ReadState(connection, transaction, nodeId, conversationId);
            transaction.Commit();
            return state;
        }, cancellationToken);
    }

    public Task<ConversationReadState> AdvanceAsync(
        HistoryMessagePosition through,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(through);
        ValidateScope(through.NodeId, through.ConversationId);
        if (through.MessageId == Guid.Empty || through.LocalSequence <= 0)
        {
            throw new ArgumentException("Read position must identify a persisted message.", nameof(through));
        }

        return writer.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            EnsureExactPosition(connection, transaction, through);
            var before = ReadState(
                connection,
                transaction,
                through.NodeId,
                through.ConversationId);
            if (before.FirstUnreadPosition is { } firstUnread &&
                firstUnread.LocalSequence <= through.LocalSequence)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE Conversations
                    SET LastReadSequence = MAX(LastReadSequence, $sequence)
                    WHERE Id = $conversationId AND NodeId = $nodeId;
                    """;
                update.Parameters.AddWithValue("$sequence", through.LocalSequence);
                update.Parameters.AddWithValue("$conversationId", through.ConversationId.ToString("D"));
                update.Parameters.AddWithValue("$nodeId", through.NodeId.ToString("D"));
                update.ExecuteNonQuery();
            }

            var after = ReadState(
                connection,
                transaction,
                through.NodeId,
                through.ConversationId);
            transaction.Commit();
            return after;
        }, cancellationToken);
    }

    private static ConversationReadState ReadState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid nodeId,
        Guid conversationId)
    {
        long watermark;
        using (var conversation = connection.CreateCommand())
        {
            conversation.Transaction = transaction;
            conversation.CommandText = """
                SELECT LastReadSequence
                FROM Conversations
                WHERE Id = $conversationId AND NodeId = $nodeId;
                """;
            conversation.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
            conversation.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
            watermark = conversation.ExecuteScalar() is long value
                ? value
                : throw new KeyNotFoundException(
                    $"Conversation '{conversationId}' does not belong to node '{nodeId}'.");
        }

        long unreadCount;
        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = """
                SELECT COUNT(*)
                FROM Messages
                WHERE ConversationId = $conversationId
                  AND Direction = $incoming
                  AND LocalSequence > $watermark;
                """;
            count.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
            count.Parameters.AddWithValue("$incoming", (int)MessageDirection.Incoming);
            count.Parameters.AddWithValue("$watermark", watermark);
            unreadCount = (long)count.ExecuteScalar()!;
        }

        HistoryMessagePosition? firstUnread = null;
        using (var first = connection.CreateCommand())
        {
            first.Transaction = transaction;
            first.CommandText = """
                SELECT Id, LocalSequence
                FROM Messages
                WHERE ConversationId = $conversationId
                  AND Direction = $incoming
                  AND LocalSequence > $watermark
                ORDER BY LocalSequence ASC
                LIMIT 1;
                """;
            first.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
            first.Parameters.AddWithValue("$incoming", (int)MessageDirection.Incoming);
            first.Parameters.AddWithValue("$watermark", watermark);
            using var result = first.ExecuteReader();
            if (result.Read())
            {
                firstUnread = new HistoryMessagePosition(
                    nodeId,
                    conversationId,
                    Guid.Parse(result.GetString(0)),
                    result.GetInt64(1));
            }
        }

        return new ConversationReadState(
            nodeId,
            conversationId,
            watermark,
            unreadCount,
            firstUnread);
    }

    private static void EnsureExactPosition(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HistoryMessagePosition position)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM Messages AS message
            JOIN Conversations AS conversation ON conversation.Id = message.ConversationId
            WHERE message.Id = $messageId
              AND message.ConversationId = $conversationId
              AND message.LocalSequence = $sequence
              AND conversation.NodeId = $nodeId;
            """;
        command.Parameters.AddWithValue("$messageId", position.MessageId.ToString("D"));
        command.Parameters.AddWithValue("$conversationId", position.ConversationId.ToString("D"));
        command.Parameters.AddWithValue("$sequence", position.LocalSequence);
        command.Parameters.AddWithValue("$nodeId", position.NodeId.ToString("D"));
        if (command.ExecuteScalar() is null)
        {
            throw new KeyNotFoundException("The read position no longer identifies a stored message.");
        }
    }

    private static void ValidateScope(Guid nodeId, Guid conversationId)
    {
        if (nodeId == Guid.Empty)
        {
            throw new ArgumentException("Node ID must not be empty.", nameof(nodeId));
        }

        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("Conversation ID must not be empty.", nameof(conversationId));
        }
    }
}
