using MeshCoreMessenger.Core.Domain;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed partial class SqliteRegionStore
{
    public Task<IReadOnlyList<RegionDiscoveryTargetOutcome>> GetDiscoveryOutcomesAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        RegionModelValidation.Identity(nodeId);
        return reader.ExecuteAsync<IReadOnlyList<RegionDiscoveryTargetOutcome>>(connection =>
        {
            using var query = Command(connection, null, """
                SELECT RunId,SessionId,Generation,RepeaterPublicKey,CompletedUtc,Status,NamesJson,Preparation,Restoration,ContactCreation,ErrorCode
                FROM RegionDiscoveryOutcomes WHERE NodeId=$node ORDER BY CompletedUtc DESC,RunId,hex(RepeaterPublicKey) LIMIT 4096
                """, ("$node", nodeId));
            using var rows = query.ExecuteReader(); var result = new List<RegionDiscoveryTargetOutcome>();
            while (rows.Read()) result.Add(new(Guid.Parse(rows.GetString(0)), nodeId, Guid.Parse(rows.GetString(1)), rows.GetInt64(2),
                Convert.ToHexString((byte[])rows[3]), Date(rows.GetString(4)), (RegionDiscoveryTargetStatus)rows.GetInt32(5),
                Array.AsReadOnly(JsonSerializer.Deserialize<string[]>(rows.GetString(6))!), (RegionRoutePreparationOutcome)rows.GetInt32(7),
                (RegionRouteRestorationOutcome)rows.GetInt32(8), (RegionContactCreationOutcome)rows.GetInt32(9), rows.IsDBNull(10) ? null : rows.GetString(10)) { Persisted = true });
            return result.AsReadOnly();
        }, cancellationToken);
    }

    public async Task SaveDiscoveryOutcomeAsync(RegionDiscoveryTargetOutcome outcome, IReadOnlyList<RegionObservation> observations,
        int maximumObservations, int maximumOutcomes, DateTimeOffset retainSinceUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome); ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(outcome.Names);
        RegionModelValidation.Identity(outcome.RunId); RegionModelValidation.Identity(outcome.NodeId); RegionModelValidation.Identity(outcome.SessionId);
        if (outcome.Generation < 0 || !Enum.IsDefined(outcome.Status) || !Enum.IsDefined(outcome.Preparation) || !Enum.IsDefined(outcome.Restoration) || !Enum.IsDefined(outcome.ContactCreation) ||
            outcome.ErrorCode?.Length > 128 || maximumObservations is < 1 or > MaximumEntries || maximumOutcomes is < 1 or > MaximumEntries)
            throw new ArgumentException("Invalid discovery outcome or retention limits.");
        var key = Convert.FromHexString(outcome.RepeaterPublicKeyHex);
        if (key.Length != 32 || outcome.Names.Count > 128 || observations.Count > MaximumEntries) throw new ArgumentException("A bounded full-key outcome is required.");
        var names = outcome.Names.ToArray(); var evidence = observations.ToArray(); var strict = new UTF8Encoding(false, true);
        if (names.Any(n => n is null || strict.GetByteCount(n) > 1024) || evidence.Any(o => o is null || o.NodeId != outcome.NodeId ||
            !o.GetRepeaterPublicKey().AsSpan().SequenceEqual(key) || !names.Contains(o.ProtocolName, StringComparer.Ordinal)))
            throw new ArgumentException("Observation ownership differs from the outcome.");
        var json = JsonSerializer.Serialize(names);
        if (Encoding.UTF8.GetByteCount(json) > 8192) throw new ArgumentException("Discovery names exceed storage bounds.");
        var at = outcome.CompletedUtc.ToUniversalTime();
        await WriteAsync((connection, tx) =>
        {
            using var insert = Command(connection, tx, """
                INSERT INTO RegionDiscoveryOutcomes(RunId,NodeId,SessionId,Generation,RepeaterPublicKey,CompletedUtc,Status,NamesJson,Preparation,Restoration,ContactCreation,ErrorCode)
                VALUES($run,$node,$session,$generation,$key,$utc,$status,$names,$preparation,$restoration,$creation,$error)
                ON CONFLICT(RunId,RepeaterPublicKey) DO NOTHING
                """, ("$run", outcome.RunId), ("$node", outcome.NodeId), ("$session", outcome.SessionId), ("$generation", outcome.Generation), ("$key", key), ("$utc", at),
                ("$status", (int)outcome.Status), ("$names", json), ("$preparation", (int)outcome.Preparation), ("$restoration", (int)outcome.Restoration), ("$creation", (int)outcome.ContactCreation), ("$error", outcome.ErrorCode));
            if (insert.ExecuteNonQuery() == 0)
            {
                using var same = Command(connection, tx, """
                    SELECT 1 FROM RegionDiscoveryOutcomes WHERE RunId=$run AND RepeaterPublicKey=$key AND NodeId=$node AND SessionId=$session
                        AND Generation=$generation AND CompletedUtc=$utc AND Status=$status AND NamesJson=$names AND Preparation=$preparation
                        AND Restoration=$restoration AND ContactCreation=$creation AND ErrorCode IS $error
                    """, ("$run", outcome.RunId), ("$key", key), ("$node", outcome.NodeId), ("$session", outcome.SessionId), ("$generation", outcome.Generation), ("$utc", at),
                    ("$status", (int)outcome.Status), ("$names", json), ("$preparation", (int)outcome.Preparation), ("$restoration", (int)outcome.Restoration), ("$creation", (int)outcome.ContactCreation), ("$error", outcome.ErrorCode));
                if (same.ExecuteScalar() is null) throw new InvalidOperationException("Discovery target already has a different terminal outcome.");
            }
            foreach (var observation in evidence) UpsertObservation(connection, tx, observation);
            PruneObservations(connection, tx, outcome.NodeId, maximumObservations, retainSinceUtc);
            using var prune = Command(connection, tx, """
                DELETE FROM RegionDiscoveryOutcomes WHERE NodeId=$node AND CompletedUtc<$since;
                DELETE FROM RegionDiscoveryOutcomes WHERE rowid IN(SELECT rowid FROM RegionDiscoveryOutcomes WHERE NodeId=$node
                    ORDER BY CompletedUtc DESC,RunId,hex(RepeaterPublicKey) LIMIT -1 OFFSET $maximum);
                """, ("$node", outcome.NodeId), ("$since", retainSinceUtc), ("$maximum", maximumOutcomes));
            prune.ExecuteNonQuery(); return true;
        }, cancellationToken).ConfigureAwait(false);
        Notify(new(outcome.NodeId));
    }

    private static void UpsertObservation(SqliteConnection connection, SqliteTransaction tx, RegionObservation item)
    {
        using var command = Command(connection, tx, """
            INSERT INTO RegionObservations VALUES($node,$key,$name,$source,$first,$last)
            ON CONFLICT(NodeId,RepeaterPublicKey,ProtocolName) DO UPDATE SET
                FirstSeenUtc=min(RegionObservations.FirstSeenUtc,excluded.FirstSeenUtc),
                LastSeenUtc=max(RegionObservations.LastSeenUtc,excluded.LastSeenUtc),
                Source=CASE WHEN excluded.LastSeenUtc>=RegionObservations.LastSeenUtc THEN excluded.Source ELSE RegionObservations.Source END
            """, ("$node", item.NodeId), ("$key", item.GetRepeaterPublicKey()), ("$name", item.ProtocolName), ("$source", (int)item.Source), ("$first", item.FirstSeenUtc), ("$last", item.LastSeenUtc));
        command.ExecuteNonQuery();
    }
    private static void PruneObservations(SqliteConnection connection, SqliteTransaction tx, Guid nodeId, int maximum, DateTimeOffset since)
    {
        using var prune = Command(connection, tx, """
            DELETE FROM RegionObservations WHERE NodeId=$node AND LastSeenUtc<$since;
            DELETE FROM RegionObservations WHERE rowid IN(SELECT rowid FROM RegionObservations WHERE NodeId=$node
                ORDER BY LastSeenUtc DESC,hex(RepeaterPublicKey),ProtocolName LIMIT -1 OFFSET $maximum);
            """, ("$node", nodeId), ("$since", since), ("$maximum", maximum));
        prune.ExecuteNonQuery();
    }
}
