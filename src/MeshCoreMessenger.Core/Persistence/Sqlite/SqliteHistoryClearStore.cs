using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed class SqliteHistoryClearStore(DatabaseWorker writer, DatabaseReader reader) : IHistoryClearStore
{
    public Task<HistoryClearStatus> GetStatusAsync(Guid nodeId, Guid conversationId, CancellationToken cancellationToken = default) =>
        reader.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var status = ReadStatus(connection, transaction, nodeId, conversationId);
            transaction.Commit();
            return status;
        }, cancellationToken);

    public Task<HistoryClearResult> ClearAsync(Guid nodeId, Guid conversationId, CancellationToken cancellationToken = default) =>
        writer.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var status = ReadStatus(connection, transaction, nodeId, conversationId);
            if (status.HasPendingSend) throw new InvalidOperationException("В этой переписке ещё выполняется отправка или ожидается подтверждение.");
            using var cutoffCommand = Command(connection, transaction, nodeId, conversationId,
                "SELECT COALESCE(MAX(LocalSequence),0) FROM Messages WHERE ConversationId=$conversation;");
            var cutoff = (long)cutoffCommand.ExecuteScalar()!;
            using var delete = Command(connection, transaction, nodeId, conversationId,
                "DELETE FROM Messages WHERE ConversationId=$conversation AND LocalSequence <= $cutoff;");
            delete.Parameters.AddWithValue("$cutoff", cutoff);
            var count = delete.ExecuteNonQuery(); // SendAttempts cascade; conversation identity and draft survive.
            using var read = Command(connection, transaction, nodeId, conversationId,
                "UPDATE Conversations SET LastReadSequence=MAX(LastReadSequence,$cutoff) WHERE Id=$conversation AND NodeId=$node;");
            read.Parameters.AddWithValue("$cutoff", cutoff);
            read.ExecuteNonQuery();
            transaction.Commit();
            return new HistoryClearResult(nodeId, conversationId, cutoff, count);
        }, cancellationToken);

    private static HistoryClearStatus ReadStatus(SqliteConnection connection, SqliteTransaction transaction, Guid nodeId, Guid conversationId)
    {
        if (nodeId == Guid.Empty || conversationId == Guid.Empty) throw new ArgumentException("An exact node and conversation are required.");
        using var command = Command(connection, transaction, nodeId, conversationId, """
            SELECT c.Kind, COALESCE(c.ContactPublicKey,ch.KeyFingerprint,c.UnknownIdentity),
                   (SELECT COUNT(*) FROM Messages m WHERE m.ConversationId=c.Id),
                   EXISTS(SELECT 1 FROM Messages m JOIN SendAttempts a ON a.MessageId=m.Id
                          WHERE m.ConversationId=c.Id AND
                          (a.State=$sending OR (a.State=$accepted AND a.AckExpectation<>$notExpected)))
            FROM Conversations c LEFT JOIN Channels ch ON ch.Id=c.ChannelId
            WHERE c.Id=$conversation AND c.NodeId=$node;
            """);
        command.Parameters.AddWithValue("$sending", (int)SendAttemptState.Sending);
        command.Parameters.AddWithValue("$accepted", (int)SendAttemptState.Accepted);
        command.Parameters.AddWithValue("$notExpected", (int)AckExpectation.NotExpected);
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new KeyNotFoundException("Переписка не принадлежит выбранной ноде.");
        return new(nodeId, conversationId, (ConversationKind)row.GetInt32(0), (byte[])row.GetValue(1), row.GetInt64(2), row.GetBoolean(3));
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, Guid node, Guid conversation, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$node", node.ToString("D"));
        command.Parameters.AddWithValue("$conversation", conversation.ToString("D"));
        return command;
    }
}
