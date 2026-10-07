using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed class SqliteMessageDetailsReader(DatabaseReader reader) : IMessageDetailsReader
{
    public Task<CommittedMessageDetails?> GetAsync(Guid nodeId, Guid conversationId, Guid messageId,
        CancellationToken cancellationToken = default)
    {
        if (nodeId == Guid.Empty || conversationId == Guid.Empty || messageId == Guid.Empty)
            throw new ArgumentException("Message scope must contain nonempty identifiers.");
        return reader.ExecuteAsync<CommittedMessageDetails?>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT m.LocalSequence, c.Kind,
                       CASE c.Kind WHEN 0 THEN contact.DisplayName WHEN 1 THEN channel.LastName ELSE NULL END,
                       m.Direction, m.MessageKind, m.TextType, m.Text
                FROM Messages m JOIN Conversations c ON c.Id = m.ConversationId
                LEFT JOIN Contacts contact ON contact.NodeId = c.NodeId AND contact.PublicKey = c.ContactPublicKey
                LEFT JOIN Channels channel ON channel.NodeId = c.NodeId AND channel.Id = c.ChannelId
                WHERE c.NodeId = $node AND c.Id = $conversation AND m.Id = $message
                """;
            command.Parameters.AddWithValue("$node", nodeId.ToString("D"));
            command.Parameters.AddWithValue("$conversation", conversationId.ToString("D"));
            command.Parameters.AddWithValue("$message", messageId.ToString("D"));
            using var result = command.ExecuteReader();
            return result.Read() ? new(nodeId, conversationId, messageId, result.GetInt64(0),
                (ConversationKind)result.GetInt32(1), result.IsDBNull(2) ? null : result.GetString(2),
                (MessageDirection)result.GetInt32(3), (StoredMessageKind)result.GetInt32(4),
                result.IsDBNull(5) ? null : result.GetInt32(5), result.IsDBNull(6) ? null : result.GetString(6)) : null;
        }, cancellationToken);
    }
}
