using System.Globalization;
using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed class SqliteLocalHistoryReader(DatabaseReader reader) : ILocalHistoryReader
{
    internal const int MaximumPageSize = 500;

    public Task<IReadOnlyList<ConversationSummary>> GetConversationsAsync(
        Guid nodeId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateNodeId(nodeId);
        ValidateLimit(limit);
        return reader.ExecuteAsync<IReadOnlyList<ConversationSummary>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.Id, c.NodeId, c.Kind, c.Title, c.IsArchived, c.UpdatedUtc,
                       m.LocalSequence, m.Direction, m.MessageKind, m.Text, m.ReceivedUtc
                FROM Conversations AS c
                LEFT JOIN Messages AS m
                  ON m.LocalSequence = (
                      SELECT MAX(latest.LocalSequence)
                      FROM Messages AS latest
                      WHERE latest.ConversationId = c.Id)
                WHERE c.NodeId = $nodeId
                ORDER BY m.LocalSequence DESC, c.UpdatedUtc DESC, c.Id ASC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
            command.Parameters.AddWithValue("$limit", limit);

            var conversations = new List<ConversationSummary>(limit);
            using var result = command.ExecuteReader();
            while (result.Read())
            {
                conversations.Add(new ConversationSummary(
                    Guid.Parse(result.GetString(0)),
                    Guid.Parse(result.GetString(1)),
                    (ConversationKind)result.GetInt32(2),
                    result.IsDBNull(3) ? null : result.GetString(3),
                    result.GetBoolean(4),
                    ParseTimestamp(result.GetString(5)),
                    result.IsDBNull(6) ? null : result.GetInt64(6),
                    result.IsDBNull(7) ? null : (MessageDirection)result.GetInt32(7),
                    result.IsDBNull(8) ? null : (StoredMessageKind)result.GetInt32(8),
                    result.IsDBNull(9) ? null : result.GetString(9),
                    result.IsDBNull(10) ? null : ParseTimestamp(result.GetString(10))));
            }

            return conversations;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<HistoryMessage>> GetMessagesAsync(
        Guid nodeId,
        Guid conversationId,
        long? beforeLocalSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateNodeId(nodeId);
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("Conversation ID must not be empty.", nameof(conversationId));
        }

        if (beforeLocalSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(beforeLocalSequence),
                "Message cursor must be positive when specified.");
        }

        ValidateLimit(limit);
        return reader.ExecuteAsync<IReadOnlyList<HistoryMessage>>(connection =>
        {
            using (var ownership = connection.CreateCommand())
            {
                ownership.CommandText = """
                    SELECT 1
                    FROM Conversations
                    WHERE Id = $conversationId AND NodeId = $nodeId;
                    """;
                ownership.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
                ownership.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
                if (ownership.ExecuteScalar() is null)
                {
                    throw new KeyNotFoundException(
                        $"Conversation '{conversationId}' does not belong to node '{nodeId}'.");
                }
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, LocalSequence, ConversationId, Direction, MessageKind, Text, ReceivedUtc
                FROM Messages
                WHERE ConversationId = $conversationId
                  AND ($beforeLocalSequence IS NULL OR LocalSequence < $beforeLocalSequence)
                ORDER BY LocalSequence DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
            command.Parameters.Add("$beforeLocalSequence", SqliteType.Integer).Value =
                (object?)beforeLocalSequence ?? DBNull.Value;
            command.Parameters.AddWithValue("$limit", limit);

            var messages = new List<HistoryMessage>(limit);
            using var result = command.ExecuteReader();
            while (result.Read())
            {
                messages.Add(new HistoryMessage(
                    Guid.Parse(result.GetString(0)),
                    result.GetInt64(1),
                    Guid.Parse(result.GetString(2)),
                    (MessageDirection)result.GetInt32(3),
                    (StoredMessageKind)result.GetInt32(4),
                    result.IsDBNull(5) ? null : result.GetString(5),
                    ParseTimestamp(result.GetString(6))));
            }

            messages.Reverse();
            return messages;
        }, cancellationToken);
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void ValidateNodeId(Guid nodeId)
    {
        if (nodeId == Guid.Empty)
        {
            throw new ArgumentException("Node ID must not be empty.", nameof(nodeId));
        }
    }

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"Page size must be between 1 and {MaximumPageSize}.");
        }
    }
}
