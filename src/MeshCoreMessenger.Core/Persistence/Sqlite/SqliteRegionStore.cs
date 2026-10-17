using System.Globalization;
using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed partial class SqliteRegionStore(DatabaseWorker writer, DatabaseReader reader) : IRegionStore
{
    private const int MaximumEntries = 4096;
    private const string RegionColumns = "Id,NodeId,Label,ProtocolName,Kind,Key,Provenance,CreatedUtc,UpdatedUtc,Archived";
    public event EventHandler<RegionDataCommit>? Changed;

    public Task<IReadOnlyList<RegionCatalogEntry>> GetRegionsAsync(Guid nodeId, bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        RegionModelValidation.Identity(nodeId);
        return reader.ExecuteAsync<IReadOnlyList<RegionCatalogEntry>>(connection =>
        {
            using var command = Command(connection, null, $"SELECT {RegionColumns} FROM Regions WHERE NodeId=$node AND ($archived=1 OR Archived=0) ORDER BY Label,Id LIMIT {MaximumEntries}", ("$node", nodeId), ("$archived", includeArchived));
            var entries = new List<RegionCatalogEntry>(); using var rows = command.ExecuteReader();
            while (rows.Read()) entries.Add(ReadRegion(rows)); return entries.AsReadOnly();
        }, cancellationToken);
    }
    public Task<RegionCatalogEntry?> GetRegionAsync(Guid nodeId, Guid regionId, CancellationToken cancellationToken = default)
    {
        RegionModelValidation.Identity(nodeId); RegionModelValidation.Identity(regionId);
        return reader.ExecuteAsync(connection => FindRegion(connection, null, nodeId, regionId), cancellationToken);
    }
    public Task<IReadOnlyList<ChannelRegionPreference>> GetRegionAssignmentsAsync(Guid nodeId, Guid regionId, CancellationToken cancellationToken = default)
    {
        RegionModelValidation.Identity(nodeId); RegionModelValidation.Identity(regionId);
        return reader.ExecuteAsync<IReadOnlyList<ChannelRegionPreference>>(connection =>
        {
            using var command = Command(connection, null, "SELECT NodeId,ChannelId,Mode,RegionId,Revision FROM ChannelRegionPreferences WHERE NodeId=$node AND RegionId=$region ORDER BY ChannelId LIMIT 4096", ("$node", nodeId), ("$region", regionId));
            var preferences = new List<ChannelRegionPreference>(); using var rows = command.ExecuteReader();
            while (rows.Read()) preferences.Add(ReadPreference(rows)); return preferences.AsReadOnly();
        }, cancellationToken);
    }
    public Task<ChannelRegionPreference> GetChannelPreferenceAsync(Guid nodeId, Guid channelId, CancellationToken cancellationToken = default)
    {
        RegionModelValidation.Identity(nodeId); RegionModelValidation.Identity(channelId);
        return reader.ExecuteAsync(connection => ReadChannelPreference(connection, null, nodeId, channelId), cancellationToken);
    }
    public async Task<RegionCatalogEntry> AddRegionAsync(RegionCatalogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var result = await WriteAsync((connection, tx) =>
        {
            using (var find = Command(connection, tx, $"SELECT {RegionColumns} FROM Regions WHERE NodeId=$node AND KeyFingerprint=$fingerprint", ("$node", entry.NodeId), ("$fingerprint", Convert.FromHexString(entry.KeyFingerprint))))
            using (var rows = find.ExecuteReader()) if (rows.Read()) return ReadRegion(rows);
            using (var count = Command(connection, tx, "SELECT COUNT(*) FROM Regions WHERE NodeId=$node", ("$node", entry.NodeId)))
                if (Convert.ToInt32(count.ExecuteScalar()) >= MaximumEntries) throw new InvalidOperationException("Region catalogue capacity reached.");
            using var insert = Command(connection, tx, """
                INSERT INTO Regions(Id,NodeId,Kind,ProtocolName,Label,Key,KeyFingerprint,Provenance,CreatedUtc,UpdatedUtc,Archived)
                VALUES($id,$node,$kind,$name,$label,$key,$fingerprint,$provenance,$created,$updated,$archived)
                """, ("$id", entry.Id), ("$node", entry.NodeId), ("$kind", (int)entry.Definition.Kind), ("$name", entry.ProtocolName),
                ("$label", entry.Label), ("$key", entry.Definition.Key.ToArray()), ("$fingerprint", Convert.FromHexString(entry.KeyFingerprint)),
                ("$provenance", (int)entry.Provenance), ("$created", entry.CreatedUtc), ("$updated", entry.UpdatedUtc), ("$archived", entry.Archived));
            insert.ExecuteNonQuery(); return entry;
        }, cancellationToken).ConfigureAwait(false);
        Notify(new(entry.NodeId, result.Id)); return result;
    }
    public Task SetRegionLabelAsync(Guid nodeId, Guid regionId, string label, DateTimeOffset updatedUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (label.Contains('\0')) throw new ArgumentException("Label contains NUL.", nameof(label));
        return UpdateRegionAsync(nodeId, regionId, "Label=$value,UpdatedUtc=$updated", label, updatedUtc, cancellationToken);
    }
    public Task SetRegionArchivedAsync(Guid nodeId, Guid regionId, bool archived, DateTimeOffset updatedUtc, CancellationToken cancellationToken = default) =>
        UpdateRegionAsync(nodeId, regionId, "Archived=$value,UpdatedUtc=$updated", archived, updatedUtc, cancellationToken);
    private async Task UpdateRegionAsync(Guid nodeId, Guid regionId, string assignments, object value, DateTimeOffset at, CancellationToken token)
    {
        RegionModelValidation.Identity(nodeId); RegionModelValidation.Identity(regionId);
        await WriteAsync((connection, tx) =>
        {
            using var command = Command(connection, tx, $"UPDATE Regions SET {assignments} WHERE NodeId=$node AND Id=$id", ("$value", value), ("$updated", at), ("$node", nodeId), ("$id", regionId));
            if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException("Region does not belong to this node."); return true;
        }, token).ConfigureAwait(false);
        Notify(new(nodeId, regionId));
    }
    public async Task DeleteRegionAsync(Guid nodeId, Guid regionId, CancellationToken cancellationToken = default)
    {
        RegionModelValidation.Identity(nodeId); RegionModelValidation.Identity(regionId);
        await WriteAsync((connection, tx) =>
        {
            using var used = Command(connection, tx, "SELECT ChannelId FROM ChannelRegionPreferences WHERE NodeId=$node AND RegionId=$region ORDER BY ChannelId LIMIT 4096", ("$node", nodeId), ("$region", regionId));
            var assignments = new List<Guid>();
            using (var rows = used.ExecuteReader()) while (rows.Read()) assignments.Add(Guid.Parse(rows.GetString(0)));
            if (assignments.Count > 0) throw new RegionInUseException(assignments);
            using var delete = Command(connection, tx, "DELETE FROM Regions WHERE NodeId=$node AND Id=$region", ("$node", nodeId), ("$region", regionId));
            return delete.ExecuteNonQuery();
        }, cancellationToken).ConfigureAwait(false);
        Notify(new(nodeId, regionId));
    }
    public async Task<ChannelRegionPreference> SetChannelPreferenceAsync(ChannelRegionPreference preference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preference);
        var updated = await WriteAsync((connection, tx) =>
        {
            var current = ReadChannelPreference(connection, tx, preference.NodeId, preference.ChannelId);
            if (current.Revision != preference.Revision) throw new RegionPreferenceConflictException();
            if (preference.RegionId is { } id)
            {
                var region = FindRegion(connection, tx, preference.NodeId, id) ?? throw new KeyNotFoundException("Region does not belong to this node.");
                if (region.Archived && (current.RegionId != id || current.Mode != FloodScopeMode.ExplicitRegion))
                    throw new InvalidOperationException("An archived region cannot be newly assigned.");
            }
            var revision = checked(preference.Revision + 1);
            using var command = Command(connection, tx, """
                INSERT INTO ChannelRegionPreferences(NodeId,ChannelId,Mode,RegionId,Revision) VALUES($node,$channel,$mode,$region,$revision)
                ON CONFLICT(NodeId,ChannelId) DO UPDATE SET Mode=excluded.Mode,RegionId=excluded.RegionId,Revision=excluded.Revision
                """, ("$node", preference.NodeId), ("$channel", preference.ChannelId), ("$mode", (int)preference.Mode), ("$region", preference.RegionId), ("$revision", revision));
            command.ExecuteNonQuery(); return new ChannelRegionPreference(preference.NodeId, preference.ChannelId, preference.Mode, preference.RegionId, revision);
        }, cancellationToken).ConfigureAwait(false);
        Notify(new(updated.NodeId, updated.RegionId, updated.ChannelId)); return updated;
    }
    public Task<NodeRegionSnapshot?> GetNodeSnapshotAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        RegionModelValidation.Identity(nodeId);
        return reader.ExecuteAsync(connection =>
        {
            using var command = Command(connection, null, "SELECT SessionId,Generation,ObservedUtc,Support,State,Name,Key FROM NodeRegionSnapshots WHERE NodeId=$node", ("$node", nodeId));
            using var rows = command.ExecuteReader(); if (!rows.Read()) return null;
            var state = (NodeFloodScopeState)rows.GetInt32(4);
            var value = state switch { NodeFloodScopeState.KnownEmpty => NodeFloodScope.KnownEmpty,
                NodeFloodScopeState.Configured => NodeFloodScope.FromPreferences(rows.GetString(5), new RegionKey((byte[])rows[6])), _ => NodeFloodScope.Unknown };
            return new NodeRegionSnapshot(nodeId, rows.IsDBNull(0) ? null : Guid.Parse(rows.GetString(0)), rows.IsDBNull(1) ? null : rows.GetInt64(1),
                rows.IsDBNull(2) ? null : Date(rows.GetString(2)), (RegionSupport)rows.GetInt32(3), value,
                state == NodeFloodScopeState.Unknown ? RegionSnapshotFreshness.Unknown : RegionSnapshotFreshness.Offline);
        }, cancellationToken);
    }
    public async Task SaveNodeSnapshotAsync(NodeRegionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await WriteAsync((connection, tx) =>
        {
            using var command = Command(connection, tx, """
                INSERT INTO NodeRegionSnapshots(NodeId,SessionId,Generation,ObservedUtc,Support,State,Name,Key)
                VALUES($node,$session,$generation,$observed,$support,$state,$name,$key)
                ON CONFLICT(NodeId) DO UPDATE SET SessionId=excluded.SessionId,Generation=excluded.Generation,ObservedUtc=excluded.ObservedUtc,
                    Support=excluded.Support,State=excluded.State,Name=excluded.Name,Key=excluded.Key
                WHERE NodeRegionSnapshots.ObservedUtc IS NULL OR (excluded.ObservedUtc IS NOT NULL AND excluded.ObservedUtc>=NodeRegionSnapshots.ObservedUtc)
                """, ("$node", snapshot.NodeId), ("$session", snapshot.SessionId), ("$generation", snapshot.Generation), ("$observed", snapshot.ObservedUtc),
                ("$support", (int)snapshot.Support), ("$state", (int)snapshot.Value.State), ("$name", snapshot.Value.Name), ("$key", snapshot.Value.Key?.ToArray()));
            return command.ExecuteNonQuery();
        }, cancellationToken).ConfigureAwait(false);
        Notify(new(snapshot.NodeId));
    }
    public Task<IReadOnlyList<RegionObservation>> GetObservationsAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        RegionModelValidation.Identity(nodeId);
        return reader.ExecuteAsync<IReadOnlyList<RegionObservation>>(connection =>
        {
            using var command = Command(connection, null, "SELECT RepeaterPublicKey,ProtocolName,Source,FirstSeenUtc,LastSeenUtc FROM RegionObservations WHERE NodeId=$node ORDER BY LastSeenUtc DESC,hex(RepeaterPublicKey),ProtocolName LIMIT 4096", ("$node", nodeId));
            var observations = new List<RegionObservation>(); using var rows = command.ExecuteReader();
            while (rows.Read()) observations.Add(new(nodeId, (byte[])rows[0], rows.GetString(1), (RegionObservationSource)rows.GetInt32(2), Date(rows.GetString(3)), Date(rows.GetString(4))));
            return observations.AsReadOnly();
        }, cancellationToken);
    }
    public async Task SaveObservationsAsync(IReadOnlyList<RegionObservation> observations, int maximumPerNode, DateTimeOffset retainSinceUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count > MaximumEntries || maximumPerNode is < 1 or > MaximumEntries) throw new ArgumentOutOfRangeException(nameof(maximumPerNode));
        var captured = observations.ToArray();
        if (captured.Any(o => o is null)) throw new ArgumentException("Null observation.", nameof(observations));
        var nodes = captured.Select(o => o.NodeId).Distinct().ToArray();
        await WriteAsync((connection, tx) =>
        {
            foreach (var item in captured) UpsertObservation(connection, tx, item);
            foreach (var node in nodes) PruneObservations(connection, tx, node, maximumPerNode, retainSinceUtc);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        foreach (var node in nodes) Notify(new(node));
    }

    private Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> action, CancellationToken token) => writer.ExecuteAsync(connection =>
    {
        using var tx = connection.BeginTransaction(); var result = action(connection, tx); tx.Commit(); return result;
    }, token);
    private void Notify(RegionDataCommit commit)
    {
        foreach (EventHandler<RegionDataCommit> handler in Changed?.GetInvocationList() ?? [])
            try { handler(this, commit); } catch { /* A callback cannot turn a committed write into a reported failure. */ }
    }
    private static ChannelRegionPreference ReadChannelPreference(SqliteConnection connection, SqliteTransaction? tx, Guid node, Guid channel)
    {
        using var exists = Command(connection, tx, "SELECT 1 FROM Channels WHERE Id=$channel AND NodeId=$node", ("$channel", channel), ("$node", node));
        if (exists.ExecuteScalar() is null) throw new KeyNotFoundException("Channel does not belong to this node.");
        using var command = Command(connection, tx, "SELECT NodeId,ChannelId,Mode,RegionId,Revision FROM ChannelRegionPreferences WHERE NodeId=$node AND ChannelId=$channel", ("$node", node), ("$channel", channel));
        using var rows = command.ExecuteReader(); return rows.Read() ? ReadPreference(rows) : new(node, channel, FloodScopeMode.InheritDefault, null, 0);
    }
    private static ChannelRegionPreference ReadPreference(SqliteDataReader rows) => new(Guid.Parse(rows.GetString(0)), Guid.Parse(rows.GetString(1)), (FloodScopeMode)rows.GetInt32(2), rows.IsDBNull(3) ? null : Guid.Parse(rows.GetString(3)), rows.GetInt64(4));
    private static RegionCatalogEntry? FindRegion(SqliteConnection connection, SqliteTransaction? tx, Guid node, Guid id)
    {
        using var command = Command(connection, tx, $"SELECT {RegionColumns} FROM Regions WHERE NodeId=$node AND Id=$id", ("$node", node), ("$id", id));
        using var rows = command.ExecuteReader(); return rows.Read() ? ReadRegion(rows) : null;
    }
    private static RegionCatalogEntry ReadRegion(SqliteDataReader rows)
    {
        var name = rows.GetString(3); var key = new RegionKey((byte[])rows[5]);
        var definition = (RegionKeyKind)rows.GetInt32(4) == RegionKeyKind.PublicDerived ? RegionDefinition.Public(name) : RegionDefinition.Custom(name, key);
        if (!definition.Key.Equals(key)) throw new InvalidDataException("Stored public region derivation disagrees with its key.");
        return new(Guid.Parse(rows.GetString(0)), Guid.Parse(rows.GetString(1)), rows.GetString(2), definition,
            (RegionProvenance)rows.GetInt32(6), Date(rows.GetString(7)), Date(rows.GetString(8)), rows.GetBoolean(9));
    }
    private static DateTimeOffset Date(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value switch { null => DBNull.Value, Guid id => id.ToString("D"), DateTimeOffset at => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), _ => value });
        return command;
    }
}
