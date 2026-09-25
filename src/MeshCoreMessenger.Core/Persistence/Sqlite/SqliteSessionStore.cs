using System.Globalization;
using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed class SqliteSessionStore(DatabaseWorker writer, DatabaseReader reader) : ISessionStore
{
    public Task<SessionRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Session ID must not be empty.", nameof(id));
        }

        return reader.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, ConnectionProfileId, NodeId, StartedUtc, EndedUtc, EndReason
                FROM Sessions
                WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            using var result = command.ExecuteReader();
            return result.Read() ? Read(result) : null;
        }, cancellationToken);
    }

    public Task CreateAsync(SessionRecord session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Id == Guid.Empty || session.ConnectionProfileId == Guid.Empty ||
            session.NodeId is { } nodeId && nodeId == Guid.Empty || session.EndedUtc is not null)
        {
            throw new ArgumentException("A new session must have valid IDs and must not already be ended.", nameof(session));
        }

        return writer.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Sessions (Id, ConnectionProfileId, NodeId, StartedUtc, EndedUtc, EndReason)
                VALUES ($id, $profileId, $nodeId, $startedUtc, NULL, NULL);
                """;
            command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
            command.Parameters.AddWithValue("$profileId", session.ConnectionProfileId.ToString("D"));
            command.Parameters.AddWithValue("$nodeId", session.NodeId is { } id ? id.ToString("D") : DBNull.Value);
            command.Parameters.AddWithValue("$startedUtc", session.StartedUtc.ToString("O"));
            command.ExecuteNonQuery();
            return true;
        }, cancellationToken);
    }

    public Task BindNodeAsync(Guid sessionId, Guid nodeId, CancellationToken cancellationToken = default)
    {
        ValidateId(sessionId, nameof(sessionId));
        ValidateId(nodeId, nameof(nodeId));
        return writer.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Sessions
                SET NodeId = $nodeId
                WHERE Id = $sessionId AND EndedUtc IS NULL AND (NodeId IS NULL OR NodeId = $nodeId);
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId.ToString("D"));
            command.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("Active session was not found or was already bound to another node.");
            }

            return true;
        }, cancellationToken);
    }

    public Task EndAsync(
        Guid sessionId,
        DateTimeOffset endedUtc,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ValidateId(sessionId, nameof(sessionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return writer.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Sessions
                SET EndedUtc = COALESCE(EndedUtc, $endedUtc),
                    EndReason = CASE WHEN EndedUtc IS NULL THEN $reason ELSE EndReason END
                WHERE Id = $sessionId;
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId.ToString("D"));
            command.Parameters.AddWithValue("$endedUtc", endedUtc.ToString("O"));
            command.Parameters.AddWithValue("$reason", reason.Trim());
            if (command.ExecuteNonQuery() != 1)
            {
                throw new KeyNotFoundException($"Session '{sessionId:D}' was not found.");
            }

            return true;
        }, cancellationToken);
    }

    private static SessionRecord Read(SqliteDataReader result) => new(
        Guid.Parse(result.GetString(0)),
        Guid.Parse(result.GetString(1)),
        result.IsDBNull(2) ? null : Guid.Parse(result.GetString(2)),
        DateTimeOffset.Parse(result.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        result.IsDBNull(4)
            ? null
            : DateTimeOffset.Parse(result.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        result.IsDBNull(5) ? null : result.GetString(5));

    private static void ValidateId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("ID must not be empty.", parameterName);
        }
    }
}
