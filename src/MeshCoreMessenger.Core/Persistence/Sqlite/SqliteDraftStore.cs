using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed class SqliteDraftStore(DatabaseWorker writer, DatabaseReader reader) : IDraftStore
{
    public Task<DraftRecord?> GetAsync(
        DraftTarget target,
        CancellationToken cancellationToken = default)
    {
        var copy = ValidateAndCopy(target);
        return reader.ExecuteAsync(connection =>
        {
            var resolved = ResolveTarget(connection, transaction: null, copy);
            if (resolved.ConversationId is not { } conversationId)
            {
                return null;
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Text, UpdatedUtc
                FROM Drafts
                WHERE ConversationId = $conversationId;
                """;
            Add(command, "$conversationId", conversationId);
            using var result = command.ExecuteReader();
            return result.Read()
                ? new DraftRecord(
                    conversationId,
                    result.GetString(0),
                    DateTimeOffset.Parse(
                        result.GetString(1),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind))
                : null;
        }, cancellationToken);
    }

    public Task<DraftRecord?> SaveAsync(
        DraftTarget target,
        string text,
        DateTimeOffset updatedUtc,
        CancellationToken cancellationToken = default)
    {
        var copy = ValidateAndCopy(target);
        ArgumentNullException.ThrowIfNull(text);
        if (updatedUtc == default)
        {
            throw new ArgumentException("Draft update time must be specified.", nameof(updatedUtc));
        }

        return writer.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                var resolved = ResolveTarget(connection, transaction, copy);
                var conversationId = resolved.ConversationId;
                if (text.Length == 0)
                {
                    if (conversationId is not null)
                    {
                        using var delete = connection.CreateCommand();
                        delete.Transaction = transaction;
                        delete.CommandText = "DELETE FROM Drafts WHERE ConversationId = $conversationId;";
                        Add(delete, "$conversationId", conversationId.Value);
                        delete.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    return null;
                }

                conversationId ??= CreateConversation(
                    connection,
                    transaction,
                    copy,
                    resolved.ChannelId,
                    updatedUtc);
                using var save = connection.CreateCommand();
                save.Transaction = transaction;
                save.CommandText = """
                    INSERT INTO Drafts (ConversationId, Text, UpdatedUtc)
                    VALUES ($conversationId, $text, $updatedUtc)
                    ON CONFLICT(ConversationId) DO UPDATE SET
                        Text = excluded.Text,
                        UpdatedUtc = excluded.UpdatedUtc;
                    """;
                Add(save, "$conversationId", conversationId.Value);
                save.Parameters.AddWithValue("$text", text);
                save.Parameters.AddWithValue("$updatedUtc", updatedUtc.ToString("O"));
                save.ExecuteNonQuery();
                transaction.Commit();
                return new DraftRecord(conversationId.Value, text, updatedUtc);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }, cancellationToken);
    }

    private static ResolvedTarget ResolveTarget(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DraftTarget target)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = target.Kind switch
        {
            ConversationKind.Contact => """
                SELECT conversation.Id, NULL
                FROM Contacts AS target
                LEFT JOIN Conversations AS conversation
                  ON conversation.NodeId = target.NodeId
                 AND conversation.Kind = $kind
                 AND conversation.ContactPublicKey = target.PublicKey
                WHERE target.NodeId = $nodeId
                  AND target.PublicKey = $identity;
                """,
            ConversationKind.Channel => """
                SELECT conversation.Id, target.Id
                FROM Channels AS target
                LEFT JOIN Conversations AS conversation
                  ON conversation.NodeId = target.NodeId
                 AND conversation.Kind = $kind
                 AND conversation.ChannelId = target.Id
                WHERE target.NodeId = $nodeId
                  AND target.KeyFingerprint = $identity;
                """,
            _ => throw new ArgumentException("Drafts are supported only for Chat contacts and known channels.", nameof(target)),
        };
        Add(command, "$nodeId", target.NodeId);
        command.Parameters.AddWithValue("$kind", (int)target.Kind);
        command.Parameters.Add("$identity", SqliteType.Blob).Value = target.Identity;
        using var result = command.ExecuteReader();
        if (!result.Read())
        {
            throw new KeyNotFoundException("The draft target is not present in the selected node directory.");
        }

        Guid? conversationId = result.IsDBNull(0) ? null : Guid.Parse(result.GetString(0));
        if (target.ConversationId is { } expected && conversationId != expected)
        {
            throw new ArgumentException("The draft conversation does not match its stable target identity.", nameof(target));
        }

        return new ResolvedTarget(
            conversationId,
            result.IsDBNull(1) ? null : Guid.Parse(result.GetString(1)));
    }

    private static Guid CreateConversation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DraftTarget target,
        Guid? channelId,
        DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Conversations (
                Id, NodeId, Kind, ContactPublicKey, ChannelId, UnknownIdentity,
                Title, IsArchived, LastReadSequence, CreatedUtc, UpdatedUtc)
            VALUES (
                $id, $nodeId, $kind, $contactKey, $channelId, NULL,
                NULL, 0, 0, $now, $now)
            ON CONFLICT DO NOTHING;
            """;
        Add(command, "$id", id);
        Add(command, "$nodeId", target.NodeId);
        command.Parameters.AddWithValue("$kind", (int)target.Kind);
        command.Parameters.Add("$contactKey", SqliteType.Blob).Value =
            target.Kind == ConversationKind.Contact ? target.Identity : DBNull.Value;
        command.Parameters.Add("$channelId", SqliteType.Text).Value =
            target.Kind == ConversationKind.Channel ? channelId!.Value.ToString("D") : DBNull.Value;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.ExecuteNonQuery();

        using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = target.Kind == ConversationKind.Contact
            ? "SELECT Id FROM Conversations WHERE NodeId = $nodeId AND Kind = 0 AND ContactPublicKey = $identity;"
            : "SELECT Id FROM Conversations WHERE NodeId = $nodeId AND Kind = 1 AND ChannelId = $channelId;";
        Add(find, "$nodeId", target.NodeId);
        find.Parameters.Add("$identity", SqliteType.Blob).Value = target.Identity;
        Add(find, "$channelId", channelId);
        return find.ExecuteScalar() is string storedId
            ? Guid.Parse(storedId)
            : throw new InvalidOperationException("The draft conversation could not be created.");
    }

    private static DraftTarget ValidateAndCopy(DraftTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.NodeId == Guid.Empty)
        {
            throw new ArgumentException("Draft node ID must not be empty.", nameof(target));
        }

        if (target.ConversationId == Guid.Empty)
        {
            throw new ArgumentException("Draft conversation ID must not be empty.", nameof(target));
        }

        if (target.Kind is not ConversationKind.Contact and not ConversationKind.Channel)
        {
            throw new ArgumentException("Drafts are supported only for Chat contacts and known channels.", nameof(target));
        }

        if (target.Identity is not { Length: 32 })
        {
            throw new ArgumentException("Draft identity must contain exactly 32 bytes.", nameof(target));
        }

        return target with { Identity = target.Identity.ToArray() };
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(
            name,
            value is Guid guid ? guid.ToString("D") : value ?? DBNull.Value);

    private sealed record ResolvedTarget(Guid? ConversationId, Guid? ChannelId);
}
