using System.Globalization;
using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed class SqliteDirectoryStore(DatabaseWorker writer, DatabaseReader reader) : IDirectoryStore
{
    public event EventHandler<ContactRouteCommit>? ContactRouteCommitted;
    public Task<DirectorySnapshotResult> ApplySnapshotAsync(
        Guid nodeId,
        Guid sessionId,
        IReadOnlyList<DirectoryContactSnapshot> contacts,
        IReadOnlyList<DirectoryChannelSnapshot> channels,
        DateTimeOffset observedUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateSnapshot(nodeId, sessionId, contacts, channels);
        observedUtc = observedUtc.ToUniversalTime();
        var contactsCopy = contacts.Select(Clone).ToArray();
        var channelsCopy = channels.OrderBy(channel => channel.Slot).Select(Clone).ToArray();
        return writer.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                EnsureSessionNode(connection, transaction, sessionId, nodeId);
                ApplyContacts(connection, transaction, nodeId, contactsCopy, observedUtc);
                var active = ReadActiveBindings(connection, transaction, nodeId)
                    .ToDictionary(binding => binding.Slot);
                var channelByFingerprint = ApplyChannels(
                    connection,
                    transaction,
                    nodeId,
                    channelsCopy,
                    observedUtc);
                var stableBindings = new List<ChannelBindingRecord>();
                var pending = new List<PendingChannelTransition>();
                foreach (var channel in channelsCopy)
                {
                    var storedChannel = channelByFingerprint[Convert.ToHexString(channel.KeyFingerprint)];
                    if (!active.TryGetValue(channel.Slot, out var previous))
                    {
                        stableBindings.Add(InsertBinding(
                            connection,
                            transaction,
                            nodeId,
                            channel.Slot,
                            generation: 1,
                            storedChannel.Id,
                            observedUtc,
                            sessionId));
                    }
                    else if (previous.ChannelId == storedChannel.Id)
                    {
                        stableBindings.Add(previous);
                    }
                    else
                    {
                        pending.Add(new PendingChannelTransition(
                            nodeId,
                            sessionId,
                            channel.Slot,
                            ChannelTransitionKind.Rebind,
                            previous,
                            storedChannel));
                    }
                }

                foreach (var previous in active.Values.Where(binding => channelsCopy.All(channel => channel.Slot != binding.Slot)))
                {
                    pending.Add(new PendingChannelTransition(
                        nodeId,
                        sessionId,
                        previous.Slot,
                        ChannelTransitionKind.Remove,
                        previous,
                        null));
                }

                var currentContacts = ReadCurrentContacts(connection, transaction, nodeId);
                transaction.Commit();
                return new DirectorySnapshotResult(
                    nodeId,
                    sessionId,
                    currentContacts,
                    stableBindings.OrderBy(binding => binding.Slot).ToArray(),
                    pending.OrderBy(transition => transition.Slot).ToArray());
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }, cancellationToken);
    }

    public async Task UpdateContactRouteAsync(Guid nodeId, Guid sessionId, ReadOnlyMemory<byte> publicKey,
        ReadOnlyMemory<byte> outPath, byte outPathLength, DateTimeOffset observedUtc, CancellationToken cancellationToken = default)
    {
        ValidateId(nodeId, nameof(nodeId));
        ValidateId(sessionId, nameof(sessionId));
        if (publicKey.Length != 32 || outPath.Length != 64) throw new ArgumentException("A complete contact key and path are required.");
        if (!ValidRouteDescriptor(outPathLength)) throw new ArgumentOutOfRangeException(nameof(outPathLength));
        var key = publicKey.ToArray(); var path = outPath.ToArray();
        await writer.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE Contacts SET OutPath = $path, OutPathLength = $length, RouteObservedUtc = $utc
                WHERE NodeId = $node AND PublicKey = $key AND PresentOnNode = 1
                  AND (RouteObservedUtc IS NULL OR RouteObservedUtc <= $utc)
                  AND EXISTS (SELECT 1 FROM Sessions WHERE Id = $session AND NodeId = $node AND EndedUtc IS NULL);
                """;
            command.Parameters.AddWithValue("$node", nodeId.ToString("D"));
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            command.Parameters.Add("$key", SqliteType.Blob).Value = key;
            command.Parameters.Add("$path", SqliteType.Blob).Value = path;
            command.Parameters.AddWithValue("$length", outPathLength);
            command.Parameters.AddWithValue("$utc", observedUtc.ToUniversalTime().ToString("O"));
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Contact/session changed or a newer route has already been observed.");
            transaction.Commit();
            return true;
        }, cancellationToken).ConfigureAwait(false);
        var commit = new ContactRouteCommit(nodeId, sessionId, key);
        foreach (EventHandler<ContactRouteCommit> handler in ContactRouteCommitted?.GetInvocationList() ?? [])
        {
            try { handler(this, commit); }
            catch { /* Observers cannot turn a committed route into an apparent write failure. */ }
        }
    }

    public Task CommitPendingChannelTransitionsAsync(
        IReadOnlyList<PendingChannelTransition> transitions,
        DateTimeOffset observedUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        if (transitions.Count == 0)
        {
            return Task.CompletedTask;
        }

        var copy = transitions.Select(Clone).OrderBy(transition => transition.Slot).ToArray();
        var nodeId = copy[0].NodeId;
        var sessionId = copy[0].SessionId;
        if (nodeId == Guid.Empty || sessionId == Guid.Empty ||
            copy.Any(transition => transition.NodeId != nodeId || transition.SessionId != sessionId) ||
            copy.GroupBy(transition => transition.Slot).Any(group => group.Count() != 1))
        {
            throw new ArgumentException("Pending channel transitions must belong to one session and contain each slot once.", nameof(transitions));
        }

        return writer.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                EnsureSessionNode(connection, transaction, sessionId, nodeId);
                foreach (var transition in copy)
                {
                    if (transition.PreviousBinding.NodeId != nodeId ||
                        transition.PreviousBinding.Slot != transition.Slot ||
                        transition.PreviousBinding.UnboundUtc is not null ||
                        transition.NextChannel is { NodeId: var nextNodeId } && nextNodeId != nodeId)
                    {
                        throw new ArgumentException("Pending channel transition is invalid.", nameof(transitions));
                    }

                    using (var close = connection.CreateCommand())
                    {
                        close.Transaction = transaction;
                        close.CommandText = """
                            UPDATE ChannelBindings
                            SET UnboundUtc = $observedUtc
                            WHERE Id = $id AND NodeId = $nodeId AND UnboundUtc IS NULL;
                            """;
                        close.Parameters.AddWithValue("$id", transition.PreviousBinding.Id.ToString("D"));
                        close.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
                        close.Parameters.AddWithValue("$observedUtc", observedUtc.ToString("O"));
                        if (close.ExecuteNonQuery() != 1)
                        {
                            throw new InvalidOperationException("The active channel binding changed before its pending transition was committed.");
                        }
                    }

                    if (transition.Kind == ChannelTransitionKind.Rebind)
                    {
                        var next = transition.NextChannel
                            ?? throw new ArgumentException("Rebind transition requires its next channel.", nameof(transitions));
                        _ = InsertBinding(
                            connection,
                            transaction,
                            nodeId,
                            transition.Slot,
                            transition.PreviousBinding.Generation + 1,
                            next.Id,
                            observedUtc,
                            sessionId);
                    }
                    else if (transition.Kind != ChannelTransitionKind.Remove || transition.NextChannel is not null)
                    {
                        throw new ArgumentException("Remove transition must not contain a next channel.", nameof(transitions));
                    }
                }

                transaction.Commit();
                return true;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ContactRecord>> GetCurrentContactsByPrefixAsync(
        Guid nodeId,
        ReadOnlyMemory<byte> publicKeyPrefix,
        CancellationToken cancellationToken = default)
    {
        ValidateId(nodeId, nameof(nodeId));
        if (publicKeyPrefix.Length != 6)
        {
            throw new ArgumentException("Contact public-key prefix must contain exactly 6 bytes.", nameof(publicKeyPrefix));
        }

        var prefix = publicKeyPrefix.ToArray();
        return reader.ExecuteAsync<IReadOnlyList<ContactRecord>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT NodeId, PublicKey, PublicKeyPrefix, DisplayName, ContactType, Flags, OutPath,
                       AdvertPayload, PresentOnNode, LastAdvertUtc, Latitude, Longitude, UpdatedUtc, OutPathLength
                FROM Contacts
                WHERE NodeId = $nodeId AND PublicKeyPrefix = $prefix AND PresentOnNode = 1
                ORDER BY PublicKey;
                """;
            command.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
            command.Parameters.Add("$prefix", SqliteType.Blob).Value = prefix;
            using var result = command.ExecuteReader();
            var contacts = new List<ContactRecord>();
            while (result.Read())
            {
                contacts.Add(ReadContact(result));
            }

            return contacts;
        }, cancellationToken);
    }

    public Task<ChannelBindingRecord?> GetActiveChannelBindingAsync(
        Guid nodeId,
        byte slot,
        CancellationToken cancellationToken = default)
    {
        ValidateId(nodeId, nameof(nodeId));
        return reader.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, NodeId, Slot, Generation, ChannelId, BoundUtc, UnboundUtc, ObservedSessionId
                FROM ChannelBindings
                WHERE NodeId = $nodeId AND Slot = $slot AND UnboundUtc IS NULL;
                """;
            command.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
            command.Parameters.AddWithValue("$slot", slot);
            using var result = command.ExecuteReader();
            return result.Read() ? ReadBinding(result) : null;
        }, cancellationToken);
    }

    private static void ApplyContacts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid nodeId,
        IReadOnlyList<DirectoryContactSnapshot> contacts,
        DateTimeOffset observedUtc)
    {
        using (var missing = connection.CreateCommand())
        {
            missing.Transaction = transaction;
            missing.CommandText = "UPDATE Contacts SET PresentOnNode = 0, UpdatedUtc = $observedUtc WHERE NodeId = $nodeId;";
            missing.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
            missing.Parameters.AddWithValue("$observedUtc", observedUtc.ToString("O"));
            missing.ExecuteNonQuery();
        }

        foreach (var contact in contacts)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Contacts (
                    NodeId, PublicKey, PublicKeyPrefix, DisplayName, ContactType, Flags, OutPath,
                    AdvertPayload, PresentOnNode, LastAdvertUtc, Latitude, Longitude, UpdatedUtc, OutPathLength, RouteObservedUtc)
                VALUES (
                    $nodeId, $publicKey, $publicKeyPrefix, $displayName, $contactType, $flags, $outPath,
                    NULL, 1, $lastAdvertUtc, $latitude, $longitude, $updatedUtc, $outPathLength, $updatedUtc)
                ON CONFLICT(NodeId, PublicKey) DO UPDATE SET
                    PublicKeyPrefix = excluded.PublicKeyPrefix,
                    DisplayName = excluded.DisplayName,
                    ContactType = excluded.ContactType,
                    Flags = excluded.Flags,
                    OutPath = CASE WHEN Contacts.RouteObservedUtc IS NULL OR excluded.RouteObservedUtc >= Contacts.RouteObservedUtc THEN excluded.OutPath ELSE Contacts.OutPath END,
                    OutPathLength = CASE WHEN Contacts.RouteObservedUtc IS NULL OR excluded.RouteObservedUtc >= Contacts.RouteObservedUtc THEN excluded.OutPathLength ELSE Contacts.OutPathLength END,
                    RouteObservedUtc = CASE WHEN Contacts.RouteObservedUtc IS NULL OR excluded.RouteObservedUtc >= Contacts.RouteObservedUtc THEN excluded.RouteObservedUtc ELSE Contacts.RouteObservedUtc END,
                    PresentOnNode = 1,
                    LastAdvertUtc = excluded.LastAdvertUtc,
                    Latitude = excluded.Latitude,
                    Longitude = excluded.Longitude,
                    UpdatedUtc = excluded.UpdatedUtc;
                """;
            command.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
            command.Parameters.Add("$publicKey", SqliteType.Blob).Value = contact.PublicKey;
            command.Parameters.Add("$publicKeyPrefix", SqliteType.Blob).Value = contact.PublicKey[..6];
            command.Parameters.AddWithValue("$displayName", contact.DisplayName);
            command.Parameters.AddWithValue("$contactType", contact.ContactType);
            command.Parameters.AddWithValue("$flags", contact.Flags);
            command.Parameters.Add("$outPath", SqliteType.Blob).Value = contact.OutPath;
            command.Parameters.AddWithValue("$outPathLength", (object?)contact.OutPathLength ?? DBNull.Value);
            command.Parameters.AddWithValue("$lastAdvertUtc", contact.LastAdvertUtc.ToString("O"));
            command.Parameters.AddWithValue("$latitude", contact.Latitude);
            command.Parameters.AddWithValue("$longitude", contact.Longitude);
            command.Parameters.AddWithValue("$updatedUtc", observedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    private static Dictionary<string, ChannelRecord> ApplyChannels(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid nodeId,
        IReadOnlyList<DirectoryChannelSnapshot> channels,
        DateTimeOffset observedUtc)
    {
        var byFingerprint = new Dictionary<string, ChannelRecord>(StringComparer.Ordinal);
        foreach (var channel in channels)
        {
            var fingerprintHex = Convert.ToHexString(channel.KeyFingerprint);
            if (byFingerprint.ContainsKey(fingerprintHex))
            {
                continue;
            }

            var existing = ReadChannelByFingerprint(connection, transaction, nodeId, channel.KeyFingerprint);
            if (existing is null)
            {
                var created = new ChannelRecord(
                    Guid.NewGuid(),
                    nodeId,
                    channel.Name,
                    channel.KeyFingerprint.ToArray(),
                    channel.AccessKind,
                    observedUtc,
                    observedUtc);
                InsertChannel(connection, transaction, created);
                byFingerprint.Add(fingerprintHex, created);
            }
            else
            {
                var updated = existing with
                {
                    LastName = channel.Name,
                    AccessKind = channel.AccessKind,
                    UpdatedUtc = observedUtc,
                };
                UpdateChannel(connection, transaction, updated);
                byFingerprint.Add(fingerprintHex, updated);
            }
        }

        return byFingerprint;
    }

    private static void InsertChannel(SqliteConnection connection, SqliteTransaction transaction, ChannelRecord channel)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Channels (Id, NodeId, LastName, KeyFingerprint, AccessKind, CreatedUtc, UpdatedUtc)
            VALUES ($id, $nodeId, $lastName, $keyFingerprint, $accessKind, $createdUtc, $updatedUtc);
            """;
        command.Parameters.AddWithValue("$id", channel.Id.ToString("D"));
        command.Parameters.AddWithValue("$nodeId", channel.NodeId.ToString("D"));
        command.Parameters.AddWithValue("$lastName", channel.LastName);
        command.Parameters.Add("$keyFingerprint", SqliteType.Blob).Value = channel.KeyFingerprint;
        command.Parameters.AddWithValue("$accessKind", (int)channel.AccessKind);
        command.Parameters.AddWithValue("$createdUtc", channel.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedUtc", channel.UpdatedUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void UpdateChannel(SqliteConnection connection, SqliteTransaction transaction, ChannelRecord channel)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Channels
            SET LastName = $lastName, AccessKind = $accessKind, UpdatedUtc = $updatedUtc
            WHERE Id = $id AND NodeId = $nodeId;
            """;
        command.Parameters.AddWithValue("$id", channel.Id.ToString("D"));
        command.Parameters.AddWithValue("$nodeId", channel.NodeId.ToString("D"));
        command.Parameters.AddWithValue("$lastName", channel.LastName);
        command.Parameters.AddWithValue("$accessKind", (int)channel.AccessKind);
        command.Parameters.AddWithValue("$updatedUtc", channel.UpdatedUtc.ToString("O"));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("Stored channel was not found for update.");
        }
    }

    private static ChannelBindingRecord InsertBinding(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid nodeId,
        byte slot,
        int generation,
        Guid channelId,
        DateTimeOffset observedUtc,
        Guid sessionId)
    {
        var binding = new ChannelBindingRecord(
            Guid.NewGuid(),
            nodeId,
            slot,
            generation,
            channelId,
            observedUtc,
            null,
            sessionId);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ChannelBindings (
                Id, NodeId, Slot, Generation, ChannelId, BoundUtc, UnboundUtc, ObservedSessionId)
            VALUES ($id, $nodeId, $slot, $generation, $channelId, $boundUtc, NULL, $sessionId);
            """;
        command.Parameters.AddWithValue("$id", binding.Id.ToString("D"));
        command.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
        command.Parameters.AddWithValue("$slot", slot);
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$channelId", channelId.ToString("D"));
        command.Parameters.AddWithValue("$boundUtc", observedUtc.ToString("O"));
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString("D"));
        command.ExecuteNonQuery();
        return binding;
    }

    private static IReadOnlyList<ChannelBindingRecord> ReadActiveBindings(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid nodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Id, NodeId, Slot, Generation, ChannelId, BoundUtc, UnboundUtc, ObservedSessionId
            FROM ChannelBindings
            WHERE NodeId = $nodeId AND UnboundUtc IS NULL;
            """;
        command.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
        using var result = command.ExecuteReader();
        var bindings = new List<ChannelBindingRecord>();
        while (result.Read())
        {
            bindings.Add(ReadBinding(result));
        }

        return bindings;
    }

    private static ChannelRecord? ReadChannelByFingerprint(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid nodeId,
        byte[] fingerprint)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Id, NodeId, LastName, KeyFingerprint, AccessKind, CreatedUtc, UpdatedUtc
            FROM Channels
            WHERE NodeId = $nodeId AND KeyFingerprint = $keyFingerprint;
            """;
        command.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
        command.Parameters.Add("$keyFingerprint", SqliteType.Blob).Value = fingerprint;
        using var result = command.ExecuteReader();
        return result.Read() ? ReadChannel(result) : null;
    }

    private static void EnsureSessionNode(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        Guid nodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM Sessions WHERE Id = $sessionId AND NodeId = $nodeId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
        if (command.ExecuteScalar() is null)
        {
            throw new InvalidOperationException("Directory snapshot does not belong to an identified session.");
        }
    }

    private static ContactRecord ReadContact(SqliteDataReader result) => new(
        Guid.Parse(result.GetString(0)),
        (byte[])result.GetValue(1),
        (byte[])result.GetValue(2),
        result.GetString(3),
        result.GetInt32(4),
        result.GetInt32(5),
        result.IsDBNull(6) ? null : (byte[])result.GetValue(6),
        result.IsDBNull(7) ? null : (byte[])result.GetValue(7),
        result.GetBoolean(8),
        result.IsDBNull(9) ? null : ParseUtc(result.GetString(9)),
        result.IsDBNull(10) ? null : result.GetDouble(10),
        result.IsDBNull(11) ? null : result.GetDouble(11),
        ParseUtc(result.GetString(12)),
        result.IsDBNull(13) ? null : result.GetByte(13));

    private static ChannelRecord ReadChannel(SqliteDataReader result) => new(
        Guid.Parse(result.GetString(0)),
        Guid.Parse(result.GetString(1)),
        result.GetString(2),
        (byte[])result.GetValue(3),
        (ChannelAccessKind)result.GetInt32(4),
        ParseUtc(result.GetString(5)),
        ParseUtc(result.GetString(6)));

    private static ChannelBindingRecord ReadBinding(SqliteDataReader result) => new(
        Guid.Parse(result.GetString(0)),
        Guid.Parse(result.GetString(1)),
        result.GetByte(2),
        result.GetInt32(3),
        Guid.Parse(result.GetString(4)),
        ParseUtc(result.GetString(5)),
        result.IsDBNull(6) ? null : ParseUtc(result.GetString(6)),
        result.IsDBNull(7) ? null : Guid.Parse(result.GetString(7)));

    private static IReadOnlyList<ContactRecord> ReadCurrentContacts(SqliteConnection connection, SqliteTransaction transaction, Guid nodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT NodeId, PublicKey, PublicKeyPrefix, DisplayName, ContactType, Flags, OutPath,
                   AdvertPayload, PresentOnNode, LastAdvertUtc, Latitude, Longitude, UpdatedUtc, OutPathLength
            FROM Contacts WHERE NodeId = $node AND PresentOnNode = 1 ORDER BY PublicKey;
            """;
        command.Parameters.AddWithValue("$node", nodeId.ToString("D"));
        using var result = command.ExecuteReader();
        var contacts = new List<ContactRecord>();
        while (result.Read()) contacts.Add(ReadContact(result));
        return contacts;
    }

    private static DirectoryContactSnapshot Clone(DirectoryContactSnapshot contact) => new(
        contact.PublicKey.ToArray(),
        contact.DisplayName,
        contact.ContactType,
        contact.Flags,
        contact.OutPath.ToArray(),
        contact.LastAdvertUtc,
        contact.Latitude,
        contact.Longitude,
        contact.OutPathLength);

    private static DirectoryChannelSnapshot Clone(DirectoryChannelSnapshot channel) => new(
        channel.Slot,
        channel.Name,
        channel.KeyFingerprint.ToArray(),
        channel.AccessKind);

    private static PendingChannelTransition Clone(PendingChannelTransition transition) => transition with
    {
        NextChannel = transition.NextChannel is null ? null : transition.NextChannel with
        {
            KeyFingerprint = transition.NextChannel.KeyFingerprint.ToArray(),
        },
    };

    private static void ValidateSnapshot(
        Guid nodeId,
        Guid sessionId,
        IReadOnlyList<DirectoryContactSnapshot> contacts,
        IReadOnlyList<DirectoryChannelSnapshot> channels)
    {
        ValidateId(nodeId, nameof(nodeId));
        ValidateId(sessionId, nameof(sessionId));
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentNullException.ThrowIfNull(channels);
        if (contacts.Any(contact => contact is null) || channels.Any(channel => channel is null))
        {
            throw new ArgumentException("Directory snapshots must not contain null items.");
        }

        if (contacts.Any(contact => contact.PublicKey.Length != 32 || contact.OutPath.Length != 64 || !ValidRouteDescriptor(contact.OutPathLength) ||
                string.IsNullOrWhiteSpace(contact.DisplayName)) ||
            contacts.GroupBy(contact => Convert.ToHexString(contact.PublicKey)).Any(group => group.Count() != 1))
        {
            throw new ArgumentException("Contacts must have unique 32-byte keys, complete paths, and names.", nameof(contacts));
        }

        if (channels.Any(channel => channel.KeyFingerprint.Length != 32 || string.IsNullOrEmpty(channel.Name)) ||
            channels.GroupBy(channel => channel.Slot).Any(group => group.Count() != 1))
        {
            throw new ArgumentException("Channels must have unique slots, names, and 32-byte fingerprints.", nameof(channels));
        }
    }

    private static bool ValidRouteDescriptor(byte? value) => value is null || value == byte.MaxValue ||
        (value.Value >> 6) != 3 && (value.Value & 0x3F) * ((value.Value >> 6) + 1) <= 64;

    private static void ValidateId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("ID must not be empty.", parameterName);
        }
    }

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
