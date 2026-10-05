using System.Buffers.Binary;
using System.Globalization;
using MeshCoreMessenger.Core.Domain;
using Microsoft.Data.Sqlite;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed class SqliteContactDeliveryHistoryReader(DatabaseReader reader) : IContactDeliveryHistoryReader
{
    public Task<ContactDeliveryPage> GetPageAsync(Guid nodeId, ReadOnlyMemory<byte> contactPublicKey, int limit = 50,
        ContactDeliveryCursor? before = null, CancellationToken cancellationToken = default)
    {
        if (nodeId == Guid.Empty || contactPublicKey.Length != 32) throw new ArgumentException("A node and full contact key are required.");
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var key = contactPublicKey.ToArray();
        if (before is not null && (before.NodeId != nodeId || before.DeliveryId == Guid.Empty || !before.ContactPublicKey.Span.SequenceEqual(key)))
            throw new ArgumentException("Cursor belongs to a different contact/node.", nameof(before));
        return reader.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = Command(connection, transaction, """
                SELECT Id,MessageId,SessionId,FirstAckReceivedUtc,PcUtcOffsetMinutes,PcTimeZoneId,WasLate,Attribution
                FROM ContactDeliveryHistory WHERE NodeId=$node AND ContactPublicKey=$key
                  AND ($time IS NULL OR FirstAckReceivedUtc<$time OR (FirstAckReceivedUtc=$time AND Id<$id))
                ORDER BY FirstAckReceivedUtc DESC,Id DESC LIMIT $limit;
                """, ("$node", nodeId), ("$key", key), ("$time", before?.AckReceivedUtc), ("$id", before?.DeliveryId), ("$limit", limit + 1));
            var items = new List<ContactDeliveryRecord>();
            using (var rows = command.ExecuteReader())
                while (rows.Read()) items.Add(new(Guid.Parse(rows.GetString(0)), nodeId, key.ToArray(), GuidOrNull(rows, 1), GuidOrNull(rows, 2),
                    Utc(rows, 3), rows.GetInt32(4), rows.GetString(5), rows.GetBoolean(6), (DeliveryAttribution)rows.GetInt32(7), []));
            var hasNext = items.Count > limit;
            if (hasNext) items.RemoveAt(items.Count - 1);
            for (var index = 0; index < items.Count; index++)
                items[index] = items[index] with { Evidence = ReadEvidence(connection, transaction, items[index].Id) };
            transaction.Commit();
            var last = items.LastOrDefault();
            return new ContactDeliveryPage(items.AsReadOnly(), hasNext && last is not null
                ? new(nodeId, key.ToArray(), last.FirstAckReceivedUtc, last.Id) : null);
        }, cancellationToken);
    }

    private static IReadOnlyList<ContactDeliveryEvidence> ReadEvidence(SqliteConnection connection, SqliteTransaction transaction, Guid delivery)
    {
        using var command = Command(connection, transaction, """
            SELECT Id,AckTag,AckReceivedUtc,RoundTripMilliseconds,Attribution,LearnedRouteDescriptor,LearnedRoutePath,LearnedRouteObservedUtc
            FROM ContactDeliveryEvidence WHERE DeliveryId=$delivery ORDER BY AckReceivedUtc,Id;
            """, ("$delivery", delivery));
        var evidence = new List<ContactDeliveryEvidence>();
        using (var rows = command.ExecuteReader())
            while (rows.Read()) evidence.Add(new(Guid.Parse(rows.GetString(0)), BinaryPrimitives.ReadUInt32LittleEndian((byte[])rows.GetValue(1)),
                Utc(rows, 2), rows.IsDBNull(3) ? null : checked((uint)rows.GetInt64(3)), (DeliveryAttribution)rows.GetInt32(4),
                ReadRoute(rows, 5, 6, 7), []));
        for (var index = 0; index < evidence.Count; index++)
        {
            using var candidates = Command(connection, transaction, """
                SELECT AttemptNumber,AttemptId,WireMessageOrdinal,WireTimestamp,WireAttempt,Phase,SentUtc,PcUtcOffsetMinutes,
                       PcTimeZoneId,RouteDescriptor,RoutePath,RouteObservedUtc,ModeReportedByMsgSent
                FROM ContactDeliveryCandidates WHERE EvidenceId=$evidence ORDER BY AttemptNumber;
                """, ("$evidence", evidence[index].Id));
            var items = new List<ContactDeliveryCandidate>();
            using var rows = candidates.ExecuteReader();
            while (rows.Read()) items.Add(new(rows.GetInt32(0), GuidOrNull(rows, 1), rows.GetInt32(2), checked((uint)rows.GetInt64(3)),
                checked((byte)rows.GetInt32(4)), (PrivateDeliveryPhase)rows.GetInt32(5), rows.IsDBNull(6) ? null : Utc(rows, 6),
                rows.IsDBNull(7) ? null : rows.GetInt32(7), rows.IsDBNull(8) ? null : rows.GetString(8), ReadRoute(rows, 9, 10, 11),
                rows.IsDBNull(12) ? null : rows.GetBoolean(12)));
            evidence[index] = evidence[index] with { Candidates = items.AsReadOnly() };
        }
        return evidence.AsReadOnly();
    }
    private static PrivateRouteSnapshot? ReadRoute(SqliteDataReader rows, int descriptor, int path, int utc) =>
        rows.IsDBNull(descriptor) || rows.IsDBNull(path) || rows.IsDBNull(utc) ? null
            : new(checked((byte)rows.GetInt32(descriptor)), (byte[])rows.GetValue(path), Utc(rows, utc));
    private static Guid? GuidOrNull(SqliteDataReader rows, int index) => rows.IsDBNull(index) ? null : Guid.Parse(rows.GetString(index));
    private static DateTimeOffset Utc(SqliteDataReader rows, int index) => DateTimeOffset.Parse(rows.GetString(index), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value switch
        {
            null => DBNull.Value, Guid guid => guid.ToString("D"), DateTimeOffset utc => utc.ToUniversalTime().ToString("O"), _ => value,
        });
        return command;
    }
}
