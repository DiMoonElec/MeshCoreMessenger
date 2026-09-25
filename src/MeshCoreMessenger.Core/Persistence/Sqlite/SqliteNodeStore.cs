using System.Globalization;
using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed class SqliteNodeStore(DatabaseWorker writer, DatabaseReader reader) : INodeStore
{
    public Task<NodeRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Node ID must not be empty.", nameof(id));
        }

        return reader.ExecuteAsync(connection => ReadSingle(connection, "Id = $value", id.ToString("D")), cancellationToken);
    }

    public Task<NodeRecord?> GetByPublicKeyAsync(
        ReadOnlyMemory<byte> publicKey,
        CancellationToken cancellationToken = default)
    {
        ValidatePublicKey(publicKey);
        var key = publicKey.ToArray();
        return reader.ExecuteAsync(connection => ReadSingle(connection, "PublicKey = $value", key), cancellationToken);
    }

    public Task<NodeRecord> FindOrCreateAsync(
        ReadOnlyMemory<byte> publicKey,
        string? name,
        DateTimeOffset seenUtc,
        CancellationToken cancellationToken = default)
    {
        ValidatePublicKey(publicKey);
        var key = publicKey.ToArray();
        var normalizedName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        return writer.ExecuteAsync(connection =>
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO Nodes (Id, PublicKey, LastName, FirstSeenUtc, LastSeenUtc)
                    VALUES ($id, $publicKey, $lastName, $seenUtc, $seenUtc)
                    ON CONFLICT(PublicKey) DO UPDATE SET
                        LastName = COALESCE(excluded.LastName, Nodes.LastName),
                        LastSeenUtc = excluded.LastSeenUtc;
                    """;
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                command.Parameters.Add("$publicKey", SqliteType.Blob).Value = key;
                command.Parameters.AddWithValue("$lastName", (object?)normalizedName ?? DBNull.Value);
                command.Parameters.AddWithValue("$seenUtc", seenUtc.ToString("O"));
                command.ExecuteNonQuery();
            }

            return ReadSingle(connection, "PublicKey = $value", key)
                ?? throw new InvalidOperationException("The node was not available after upsert.");
        }, cancellationToken);
    }

    private static NodeRecord? ReadSingle(SqliteConnection connection, string predicate, object value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, PublicKey, LastName, FirstSeenUtc, LastSeenUtc
            FROM Nodes
            WHERE {predicate};
            """;
        if (value is byte[] bytes)
        {
            command.Parameters.Add("$value", SqliteType.Blob).Value = bytes;
        }
        else
        {
            command.Parameters.AddWithValue("$value", value);
        }

        using var result = command.ExecuteReader();
        return result.Read() ? Read(result) : null;
    }

    private static NodeRecord Read(SqliteDataReader result) => new(
        Guid.Parse(result.GetString(0)),
        (byte[])result.GetValue(1),
        result.IsDBNull(2) ? null : result.GetString(2),
        DateTimeOffset.Parse(result.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(result.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static void ValidatePublicKey(ReadOnlyMemory<byte> publicKey)
    {
        if (publicKey.Length != 32)
        {
            throw new ArgumentException("Node public key must contain exactly 32 bytes.", nameof(publicKey));
        }
    }
}
