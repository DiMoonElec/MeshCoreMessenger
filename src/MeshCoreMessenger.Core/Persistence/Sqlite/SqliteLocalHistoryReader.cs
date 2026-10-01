using System.Globalization;
using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed class SqliteLocalHistoryReader(DatabaseReader reader) : ILocalHistoryReader
{
    internal const int MaximumPageSize = 500;
    internal const int MaximumSearchPageSize = 100;
    private const string MessageColumns =
        "Id, LocalSequence, ConversationId, Direction, MessageKind, Text, ReceivedUtc, " +
        "TextType, BinaryDataType, WireTimestamp, ResolutionState";

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
        ValidateScope(nodeId, conversationId);
        if (beforeLocalSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(beforeLocalSequence),
                "Message cursor must be positive when specified.");
        }

        ValidateLimit(limit);
        return reader.ExecuteAsync<IReadOnlyList<HistoryMessage>>(connection =>
        {
            EnsureConversationOwnership(connection, nodeId, conversationId);
            using var command = connection.CreateCommand();
            command.CommandText = beforeLocalSequence is null
                ? $"""
                    SELECT {MessageColumns}
                    FROM Messages
                    WHERE ConversationId = $conversationId
                    ORDER BY LocalSequence DESC
                    LIMIT $limit;
                    """
                : $"""
                    SELECT {MessageColumns}
                    FROM Messages
                    WHERE ConversationId = $conversationId
                      AND LocalSequence < $beforeLocalSequence
                    ORDER BY LocalSequence DESC
                    LIMIT $limit;
                    """;
            command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
            if (beforeLocalSequence is { } sequence)
            {
                command.Parameters.AddWithValue("$beforeLocalSequence", sequence);
            }
            command.Parameters.AddWithValue("$limit", limit);
            var messages = ReadMessages(command, limit);
            messages.Reverse();
            return messages;
        }, cancellationToken);
    }

    public Task<HistoryMessagePosition?> GetMessagePositionAsync(
        Guid nodeId,
        Guid conversationId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(nodeId, conversationId);
        if (messageId == Guid.Empty)
        {
            throw new ArgumentException("Message ID must not be empty.", nameof(messageId));
        }

        return reader.ExecuteAsync<HistoryMessagePosition?>(connection =>
        {
            EnsureConversationOwnership(connection, nodeId, conversationId);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT LocalSequence
                FROM Messages
                WHERE Id = $messageId AND ConversationId = $conversationId;
                """;
            command.Parameters.AddWithValue("$messageId", messageId.ToString("D"));
            command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
            return command.ExecuteScalar() is long sequence
                ? new HistoryMessagePosition(nodeId, conversationId, messageId, sequence)
                : null;
        }, cancellationToken);
    }

    public Task<HistoryMessagePage> GetMessagesBeforeAsync(
        Guid nodeId,
        Guid conversationId,
        HistoryMessagePosition? before,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(nodeId, conversationId);
        ValidatePositionScope(before, nodeId, conversationId, nameof(before));
        ValidateLimit(limit);
        return reader.ExecuteAsync(connection =>
        {
            EnsureConversationOwnership(connection, nodeId, conversationId);
            if (before is not null)
            {
                EnsureExactPosition(connection, before);
            }

            using var command = connection.CreateCommand();
            command.CommandText = before is null
                ? $"""
                    SELECT {MessageColumns}
                    FROM Messages
                    WHERE ConversationId = $conversationId
                    ORDER BY LocalSequence DESC
                    LIMIT $limit;
                    """
                : $"""
                    SELECT {MessageColumns}
                    FROM Messages
                    WHERE ConversationId = $conversationId
                      AND LocalSequence < $sequence
                    ORDER BY LocalSequence DESC
                    LIMIT $limit;
                    """;
            command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
            if (before is not null)
            {
                command.Parameters.AddWithValue("$sequence", before.LocalSequence);
            }
            command.Parameters.AddWithValue("$limit", limit + 1);
            var descending = ReadMessages(command, limit + 1);
            var hasEarlier = descending.Count > limit;
            if (hasEarlier)
            {
                descending.RemoveAt(descending.Count - 1);
            }

            descending.Reverse();
            return CreatePage(nodeId, conversationId, descending, hasEarlier, before is not null);
        }, cancellationToken);
    }

    public Task<HistoryMessagePage> GetMessagesAfterAsync(
        Guid nodeId,
        Guid conversationId,
        HistoryMessagePosition? after,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(nodeId, conversationId);
        ValidatePositionScope(after, nodeId, conversationId, nameof(after));
        ValidateLimit(limit);
        return reader.ExecuteAsync(connection =>
        {
            EnsureConversationOwnership(connection, nodeId, conversationId);
            if (after is not null)
            {
                EnsureExactPosition(connection, after);
            }

            using var command = connection.CreateCommand();
            command.CommandText = after is null
                ? $"""
                    SELECT {MessageColumns}
                    FROM Messages
                    WHERE ConversationId = $conversationId
                    ORDER BY LocalSequence ASC
                    LIMIT $limit;
                    """
                : $"""
                    SELECT {MessageColumns}
                    FROM Messages
                    WHERE ConversationId = $conversationId
                      AND LocalSequence > $sequence
                    ORDER BY LocalSequence ASC
                    LIMIT $limit;
                    """;
            command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
            if (after is not null)
            {
                command.Parameters.AddWithValue("$sequence", after.LocalSequence);
            }
            command.Parameters.AddWithValue("$limit", limit + 1);
            var messages = ReadMessages(command, limit + 1);
            var hasLater = messages.Count > limit;
            if (hasLater)
            {
                messages.RemoveAt(messages.Count - 1);
            }

            return CreatePage(nodeId, conversationId, messages, after is not null, hasLater);
        }, cancellationToken);
    }

    public Task<HistoryMessagePage> GetMessagesAroundAsync(
        HistoryMessagePosition position,
        int beforeLimit,
        int afterLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(position);
        ValidateScope(position.NodeId, position.ConversationId);
        ValidatePosition(position, nameof(position));
        if (beforeLimit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(beforeLimit));
        }

        if (afterLimit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterLimit));
        }

        if ((long)beforeLimit + afterLimit + 1 > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(afterLimit),
                $"An around page may contain at most {MaximumPageSize} messages.");
        }

        return reader.ExecuteAsync(connection =>
        {
            EnsureConversationOwnership(connection, position.NodeId, position.ConversationId);
            var anchor = ReadExactPositionMessage(connection, position);
            var olderDescending = ReadRelative(
                connection,
                position.ConversationId,
                position.LocalSequence,
                before: true,
                beforeLimit + 1);
            var hasEarlier = olderDescending.Count > beforeLimit;
            if (hasEarlier)
            {
                olderDescending.RemoveAt(olderDescending.Count - 1);
            }

            olderDescending.Reverse();
            var newer = ReadRelative(
                connection,
                position.ConversationId,
                position.LocalSequence,
                before: false,
                afterLimit + 1);
            var hasLater = newer.Count > afterLimit;
            if (hasLater)
            {
                newer.RemoveAt(newer.Count - 1);
            }

            var messages = new List<HistoryMessage>(olderDescending.Count + 1 + newer.Count);
            messages.AddRange(olderDescending);
            messages.Add(anchor);
            messages.AddRange(newer);
            return CreatePage(
                position.NodeId,
                position.ConversationId,
                messages,
                hasEarlier,
                hasLater);
        }, cancellationToken);
    }

    public Task<HistoryMessageSearchPage> SearchMessagesAsync(
        Guid nodeId,
        Guid conversationId,
        string query,
        HistoryMessagePosition? before,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(nodeId, conversationId);
        ArgumentNullException.ThrowIfNull(query);
        ValidatePositionScope(before, nodeId, conversationId, nameof(before));
        if (limit is < 1 or > MaximumSearchPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"Search page size must be between 1 and {MaximumSearchPageSize}.");
        }

        if (query.Length == 0)
        {
            return Task.FromResult(new HistoryMessageSearchPage([], null));
        }

        return reader.ExecuteAsync(connection =>
        {
            EnsureConversationOwnership(connection, nodeId, conversationId);
            if (before is not null)
            {
                EnsureExactPosition(connection, before);
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, LocalSequence, Direction, Text, ReceivedUtc
                FROM Messages
                WHERE ConversationId = $conversationId
                  AND MessageKind = $textKind
                  AND Text IS NOT NULL
                  AND instr(Text, $query) > 0
                  AND ($beforeSequence IS NULL OR LocalSequence < $beforeSequence)
                ORDER BY LocalSequence DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
            command.Parameters.AddWithValue("$textKind", (int)StoredMessageKind.Text);
            command.Parameters.AddWithValue("$query", query);
            command.Parameters.Add("$beforeSequence", SqliteType.Integer).Value =
                before is null ? DBNull.Value : before.LocalSequence;
            command.Parameters.AddWithValue("$limit", limit + 1);

            var results = new List<HistoryMessageSearchResult>(limit + 1);
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                var messageId = Guid.Parse(rows.GetString(0));
                var sequence = rows.GetInt64(1);
                results.Add(new HistoryMessageSearchResult(
                    new HistoryMessagePosition(nodeId, conversationId, messageId, sequence),
                    (MessageDirection)rows.GetInt32(2),
                    rows.GetString(3),
                    ParseTimestamp(rows.GetString(4))));
            }

            HistoryMessagePosition? nextCursor = null;
            if (results.Count > limit)
            {
                results.RemoveAt(results.Count - 1);
                nextCursor = results[^1].Position;
            }

            return new HistoryMessageSearchPage(results, nextCursor);
        }, cancellationToken);
    }

    private static List<HistoryMessage> ReadRelative(
        SqliteConnection connection,
        Guid conversationId,
        long sequence,
        bool before,
        int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {MessageColumns}
            FROM Messages
            WHERE ConversationId = $conversationId
              AND LocalSequence {(before ? "<" : ">")} $sequence
            ORDER BY LocalSequence {(before ? "DESC" : "ASC")}
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$limit", limit);
        return ReadMessages(command, limit);
    }

    private static HistoryMessage ReadExactPositionMessage(
        SqliteConnection connection,
        HistoryMessagePosition position)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {MessageColumns}
            FROM Messages
            WHERE Id = $messageId
              AND ConversationId = $conversationId
              AND LocalSequence = $sequence;
            """;
        command.Parameters.AddWithValue("$messageId", position.MessageId.ToString("D"));
        command.Parameters.AddWithValue("$conversationId", position.ConversationId.ToString("D"));
        command.Parameters.AddWithValue("$sequence", position.LocalSequence);
        var messages = ReadMessages(command, 1);
        return messages.Count == 1
            ? messages[0]
            : throw new KeyNotFoundException("The message position no longer identifies a stored message.");
    }

    private static void EnsureExactPosition(SqliteConnection connection, HistoryMessagePosition position) =>
        _ = ReadExactPositionMessage(connection, position);

    private static List<HistoryMessage> ReadMessages(SqliteCommand command, int capacity)
    {
        var messages = new List<HistoryMessage>(capacity);
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
                ParseTimestamp(result.GetString(6)))
            {
                TextType = result.IsDBNull(7) ? null : result.GetInt32(7),
                BinaryDataType = result.IsDBNull(8) ? null : checked((ushort)result.GetInt32(8)),
                WireTimestamp = result.IsDBNull(9) ? null : result.GetInt64(9),
                ResolutionState = (MessageResolutionState)result.GetInt32(10),
            });
        }

        return messages;
    }

    private static HistoryMessagePage CreatePage(
        Guid nodeId,
        Guid conversationId,
        IReadOnlyList<HistoryMessage> messages,
        bool hasEarlier,
        bool hasLater) =>
        new(
            messages,
            messages.Count == 0 ? null : Position(nodeId, conversationId, messages[0]),
            messages.Count == 0 ? null : Position(nodeId, conversationId, messages[^1]),
            hasEarlier,
            hasLater);

    private static HistoryMessagePosition Position(
        Guid nodeId,
        Guid conversationId,
        HistoryMessage message) =>
        new(nodeId, conversationId, message.Id, message.LocalSequence);

    private static void EnsureConversationOwnership(
        SqliteConnection connection,
        Guid nodeId,
        Guid conversationId)
    {
        using var ownership = connection.CreateCommand();
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

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void ValidateScope(Guid nodeId, Guid conversationId)
    {
        ValidateNodeId(nodeId);
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("Conversation ID must not be empty.", nameof(conversationId));
        }
    }

    private static void ValidatePositionScope(
        HistoryMessagePosition? position,
        Guid nodeId,
        Guid conversationId,
        string parameterName)
    {
        if (position is null)
        {
            return;
        }

        ValidatePosition(position, parameterName);
        if (position.NodeId != nodeId || position.ConversationId != conversationId)
        {
            throw new ArgumentException(
                "The message position belongs to another node or conversation.",
                parameterName);
        }
    }

    private static void ValidatePosition(HistoryMessagePosition position, string parameterName)
    {
        if (position.NodeId == Guid.Empty ||
            position.ConversationId == Guid.Empty ||
            position.MessageId == Guid.Empty ||
            position.LocalSequence <= 0)
        {
            throw new ArgumentException("The message position is incomplete or invalid.", parameterName);
        }
    }

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
