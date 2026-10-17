using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Models;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed partial class SqliteOutgoingMessageStore
{
    public Task<ResolvedRegionScope> GetAttemptScopeAsync(Guid nodeId, Guid attemptId, CancellationToken cancellationToken = default) =>
        reader.ExecuteAsync(connection => ReadAttemptScope(connection, null, nodeId, attemptId), cancellationToken);

    private static ResolvedRegionScope ReadAttemptScope(SqliteConnection connection, SqliteTransaction? transaction, Guid nodeId, Guid attemptId)
    {
        using var query = Command(connection, transaction, """
            SELECT s.SessionId,s.Generation,s.RequestedMode,s.PolicyRevision,s.State,s.Key,s.Name,s.MaximumTextBytes
            FROM SendAttemptScopes s WHERE s.AttemptId=$attempt AND s.NodeId=$node;
            """, ("$attempt", attemptId), ("$node", nodeId));
        using var row = query.ExecuteReader();
        if (!row.Read()) throw new KeyNotFoundException("Attempt scope does not belong to this node.");
        return new(nodeId, row.IsDBNull(0) ? null : Guid.Parse(row.GetString(0)), row.IsDBNull(1) ? null : row.GetInt64(1),
            (FloodScopeMode)row.GetInt32(2), row.GetInt64(3), (StoredRegionScopeState)row.GetInt32(4),
            row.IsDBNull(5) ? null : new RegionKey((byte[])row[5]), row.IsDBNull(6) ? null : row.GetString(6), row.IsDBNull(7) ? null : row.GetInt32(7));
    }

    private static ResolvedRegionScope ScopeOrLegacy(ResolvedRegionScope? scope, Guid nodeId, Guid sessionId, int? budget = null)
    {
        if (scope is null) return new(nodeId, sessionId, null, FloodScopeMode.InheritDefault, 0, StoredRegionScopeState.LegacyUnknown, null, null, null);
        if (scope.NodeId != nodeId || scope.SessionId != sessionId || budget is { } bytes && scope.MaximumTextBytes != bytes)
            throw new ArgumentException("Scope capture differs from preparation ownership or budget.", nameof(scope));
        return scope;
    }

    private static void InsertAttemptScope(SqliteConnection connection, SqliteTransaction transaction, Guid attemptId, ResolvedRegionScope scope)
    {
        var key = scope.Key?.ToArray();
        using var command = Command(connection, transaction, """
            INSERT INTO SendAttemptScopes(AttemptId,NodeId,SessionId,Generation,RequestedMode,PolicyRevision,State,Key,KeyFingerprint,Name,MaximumTextBytes)
            VALUES($attempt,$node,$session,$generation,$mode,$revision,$state,$key,$fingerprint,$name,$budget);
            """, ("$attempt", attemptId), ("$node", scope.NodeId), ("$session", scope.SessionId), ("$generation", scope.Generation),
            ("$mode", (int)scope.RequestedMode), ("$revision", scope.PolicyRevision), ("$state", (int)scope.State), ("$key", key),
            ("$fingerprint", key is null ? null : SHA256.HashData(key)), ("$name", scope.Name), ("$budget", scope.MaximumTextBytes));
        command.ExecuteNonQuery();
    }

    private static void CheckScopeEqual(ResolvedRegionScope expected, ResolvedRegionScope actual)
    {
        if (expected.NodeId != actual.NodeId || expected.SessionId != actual.SessionId || expected.Generation != actual.Generation ||
            expected.RequestedMode != actual.RequestedMode || expected.PolicyRevision != actual.PolicyRevision || expected.State != actual.State ||
            !Equals(expected.Key, actual.Key) || expected.Name != actual.Name || expected.MaximumTextBytes != actual.MaximumTextBytes)
            throw new InvalidOperationException("Preparation already has a different immutable scope capture.");
    }
}
