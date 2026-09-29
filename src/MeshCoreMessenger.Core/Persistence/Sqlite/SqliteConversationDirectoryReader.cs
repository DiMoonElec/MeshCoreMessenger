using System.Globalization;
using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed class SqliteConversationDirectoryReader(DatabaseReader reader) : IConversationDirectoryReader
{
    internal const int MaximumPageSize = 200;

    public Task<ConversationDirectoryPage> GetPageAsync(
        Guid nodeId,
        ConversationDirectorySection section,
        ConversationDirectoryCursor? after,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateNodeId(nodeId);
        ValidateSection(section);
        ValidateCursor(after, nodeId, section);
        ValidateLimit(limit);

        return reader.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = BuildPageQuery(section);
            command.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
            command.Parameters.AddWithValue("$chatContactType", (int)AdvertisementType.Chat);
            command.Parameters.Add("$cursorSequence", SqliteType.Integer).Value =
                after is null ? DBNull.Value : after.ActivitySequence;
            command.Parameters.Add("$cursorUtc", SqliteType.Text).Value =
                after is null ? DBNull.Value : after.ActivityUtc.ToString("O");
            command.Parameters.Add("$cursorStableKey", SqliteType.Text).Value =
                after is null ? DBNull.Value : after.StableKey;
            command.Parameters.AddWithValue("$limit", limit + 1);

            var entries = new List<ConversationDirectoryEntry>(limit + 1);
            using var result = command.ExecuteReader();
            while (result.Read())
            {
                entries.Add(ReadEntry(result, section));
            }

            ConversationDirectoryCursor? nextCursor = null;
            if (entries.Count > limit)
            {
                entries.RemoveAt(entries.Count - 1);
                var last = entries[^1];
                nextCursor = new ConversationDirectoryCursor(
                    last.NodeId,
                    last.Section,
                    last.ActivitySequence,
                    last.ActivityUtc,
                    last.StableKey);
            }

            return new ConversationDirectoryPage(entries, nextCursor);
        }, cancellationToken);
    }

    public Task<ContactDetailsProjection?> GetContactDetailsAsync(
        Guid nodeId,
        ReadOnlyMemory<byte> publicKey,
        CancellationToken cancellationToken = default)
    {
        ValidateNodeId(nodeId);
        ValidateIdentity(publicKey, nameof(publicKey));
        var key = publicKey.ToArray();
        return reader.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT NodeId, PublicKey, DisplayName, ContactType, Flags, OutPath,
                       AdvertPayload, PresentOnNode, LastAdvertUtc, Latitude, Longitude, UpdatedUtc
                FROM Contacts
                WHERE NodeId = $nodeId AND PublicKey = $publicKey;
                """;
            command.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
            command.Parameters.Add("$publicKey", SqliteType.Blob).Value = key;
            using var result = command.ExecuteReader();
            if (!result.Read())
            {
                return null;
            }

            return new ContactDetailsProjection(
                Guid.Parse(result.GetString(0)),
                ((byte[])result.GetValue(1)).ToArray(),
                result.GetString(2),
                result.GetInt32(3),
                result.GetInt32(4),
                result.IsDBNull(5) ? null : ((byte[])result.GetValue(5)).ToArray(),
                result.IsDBNull(6) ? null : ((byte[])result.GetValue(6)).ToArray(),
                result.GetBoolean(7),
                result.IsDBNull(8) ? null : ParseTimestamp(result.GetString(8)),
                result.IsDBNull(9) ? null : result.GetDouble(9),
                result.IsDBNull(10) ? null : result.GetDouble(10),
                ParseTimestamp(result.GetString(11)));
        }, cancellationToken);
    }

    public Task<ChannelDetailsProjection?> GetChannelDetailsAsync(
        Guid nodeId,
        ReadOnlyMemory<byte> keyFingerprint,
        CancellationToken cancellationToken = default)
    {
        ValidateNodeId(nodeId);
        ValidateIdentity(keyFingerprint, nameof(keyFingerprint));
        var fingerprint = keyFingerprint.ToArray();
        return reader.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ch.Id, ch.NodeId, ch.LastName, ch.KeyFingerprint, ch.AccessKind,
                       ch.CreatedUtc, ch.UpdatedUtc,
                       (SELECT group_concat(active.Slot, ',')
                        FROM (
                            SELECT b.Slot
                            FROM ChannelBindings AS b
                            WHERE b.NodeId = ch.NodeId
                              AND b.ChannelId = ch.Id
                              AND b.UnboundUtc IS NULL
                            ORDER BY b.Slot
                        ) AS active)
                FROM Channels AS ch
                WHERE ch.NodeId = $nodeId AND ch.KeyFingerprint = $keyFingerprint;
                """;
            command.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
            command.Parameters.Add("$keyFingerprint", SqliteType.Blob).Value = fingerprint;
            using var result = command.ExecuteReader();
            if (!result.Read())
            {
                return null;
            }

            return new ChannelDetailsProjection(
                Guid.Parse(result.GetString(0)),
                Guid.Parse(result.GetString(1)),
                result.GetString(2),
                ((byte[])result.GetValue(3)).ToArray(),
                (ChannelAccessKind)result.GetInt32(4),
                ParseSlots(result.IsDBNull(7) ? null : result.GetString(7)),
                ParseTimestamp(result.GetString(5)),
                ParseTimestamp(result.GetString(6)));
        }, cancellationToken);
    }

    private static string BuildPageQuery(ConversationDirectorySection section)
    {
        var entries = section switch
        {
            ConversationDirectorySection.ChatContacts => ContactEntries("contact.ContactType = $chatContactType"),
            ConversationDirectorySection.ServiceContacts => ContactEntries("contact.ContactType <> $chatContactType"),
            ConversationDirectorySection.Channels => ChannelEntries(),
            ConversationDirectorySection.UnknownContacts => UnknownEntries((int)ConversationKind.UnknownContact, "unknown-contact:"),
            ConversationDirectorySection.UnknownChannels => UnknownEntries((int)ConversationKind.UnknownChannel, "unknown-channel:"),
            _ => throw new ArgumentOutOfRangeException(nameof(section)),
        };

        return $"""
            WITH entries AS (
                {entries}
            )
            SELECT StableKey, NodeId, Kind, Identity, ConversationId, DisplayName,
                   ContactType, PresentOnNode, AccessKind, ActiveSlots, IsArchived,
                   UpdatedUtc, ActivitySequence, ActivityUtc, LastMessageSequence,
                   LastMessageDirection, LastMessageKind, LastMessageResolutionState,
                   LastMessageText, LastMessageUtc
            FROM entries
            WHERE $cursorSequence IS NULL
               OR ActivitySequence < $cursorSequence
               OR (ActivitySequence = $cursorSequence
                   AND julianday(ActivityUtc) < julianday($cursorUtc))
               OR (ActivitySequence = $cursorSequence
                   AND julianday(ActivityUtc) = julianday($cursorUtc)
                   AND StableKey > $cursorStableKey)
            ORDER BY ActivitySequence DESC, julianday(ActivityUtc) DESC, StableKey ASC
            LIMIT $limit;
            """;
    }

    private static string ContactEntries(string contactFilter) => $"""
        SELECT 'contact:' || lower(hex(contact.PublicKey)) AS StableKey,
               contact.NodeId AS NodeId,
               {(int)ConversationKind.Contact} AS Kind,
               contact.PublicKey AS Identity,
               conversation.Id AS ConversationId,
               contact.DisplayName AS DisplayName,
               contact.ContactType AS ContactType,
               contact.PresentOnNode AS PresentOnNode,
               NULL AS AccessKind,
               NULL AS ActiveSlots,
               COALESCE(conversation.IsArchived, 0) AS IsArchived,
               CASE
                   WHEN conversation.UpdatedUtc > contact.UpdatedUtc THEN conversation.UpdatedUtc
                   ELSE contact.UpdatedUtc
               END AS UpdatedUtc,
               COALESCE(message.LocalSequence, 0) AS ActivitySequence,
               CASE
                   WHEN message.LocalSequence IS NOT NULL THEN message.ReceivedUtc
                   WHEN conversation.UpdatedUtc > contact.UpdatedUtc THEN conversation.UpdatedUtc
                   ELSE contact.UpdatedUtc
               END AS ActivityUtc,
               message.LocalSequence AS LastMessageSequence,
               message.Direction AS LastMessageDirection,
               message.MessageKind AS LastMessageKind,
               message.ResolutionState AS LastMessageResolutionState,
               message.Text AS LastMessageText,
               message.ReceivedUtc AS LastMessageUtc
        FROM Contacts AS contact
        LEFT JOIN Conversations AS conversation
          ON conversation.NodeId = contact.NodeId
         AND conversation.Kind = {(int)ConversationKind.Contact}
         AND conversation.ContactPublicKey = contact.PublicKey
        LEFT JOIN Messages AS message
          ON message.LocalSequence = (
              SELECT MAX(latest.LocalSequence)
              FROM Messages AS latest
              WHERE latest.ConversationId = conversation.Id)
        WHERE contact.NodeId = $nodeId AND {contactFilter}
        """;

    private static string ChannelEntries() => $"""
        SELECT 'channel:' || lower(hex(channel.KeyFingerprint)) AS StableKey,
               channel.NodeId AS NodeId,
               {(int)ConversationKind.Channel} AS Kind,
               channel.KeyFingerprint AS Identity,
               conversation.Id AS ConversationId,
               channel.LastName AS DisplayName,
               NULL AS ContactType,
               NULL AS PresentOnNode,
               channel.AccessKind AS AccessKind,
               (SELECT group_concat(active.Slot, ',')
                FROM (
                    SELECT binding.Slot
                    FROM ChannelBindings AS binding
                    WHERE binding.NodeId = channel.NodeId
                      AND binding.ChannelId = channel.Id
                      AND binding.UnboundUtc IS NULL
                    ORDER BY binding.Slot
                ) AS active) AS ActiveSlots,
               COALESCE(conversation.IsArchived, 0) AS IsArchived,
               CASE
                   WHEN conversation.UpdatedUtc > channel.UpdatedUtc THEN conversation.UpdatedUtc
                   ELSE channel.UpdatedUtc
               END AS UpdatedUtc,
               COALESCE(message.LocalSequence, 0) AS ActivitySequence,
               CASE
                   WHEN message.LocalSequence IS NOT NULL THEN message.ReceivedUtc
                   WHEN conversation.UpdatedUtc > channel.UpdatedUtc THEN conversation.UpdatedUtc
                   ELSE channel.UpdatedUtc
               END AS ActivityUtc,
               message.LocalSequence AS LastMessageSequence,
               message.Direction AS LastMessageDirection,
               message.MessageKind AS LastMessageKind,
               message.ResolutionState AS LastMessageResolutionState,
               message.Text AS LastMessageText,
               message.ReceivedUtc AS LastMessageUtc
        FROM Channels AS channel
        LEFT JOIN Conversations AS conversation
          ON conversation.NodeId = channel.NodeId
         AND conversation.Kind = {(int)ConversationKind.Channel}
         AND conversation.ChannelId = channel.Id
        LEFT JOIN Messages AS message
          ON message.LocalSequence = (
              SELECT MAX(latest.LocalSequence)
              FROM Messages AS latest
              WHERE latest.ConversationId = conversation.Id)
        WHERE channel.NodeId = $nodeId
        """;

    private static string UnknownEntries(int kind, string stableKeyPrefix) => $"""
        SELECT '{stableKeyPrefix}' || lower(hex(conversation.UnknownIdentity)) AS StableKey,
               conversation.NodeId AS NodeId,
               conversation.Kind AS Kind,
               conversation.UnknownIdentity AS Identity,
               conversation.Id AS ConversationId,
               conversation.Title AS DisplayName,
               NULL AS ContactType,
               NULL AS PresentOnNode,
               NULL AS AccessKind,
               NULL AS ActiveSlots,
               conversation.IsArchived AS IsArchived,
               conversation.UpdatedUtc AS UpdatedUtc,
               COALESCE(message.LocalSequence, 0) AS ActivitySequence,
               COALESCE(message.ReceivedUtc, conversation.UpdatedUtc) AS ActivityUtc,
               message.LocalSequence AS LastMessageSequence,
               message.Direction AS LastMessageDirection,
               message.MessageKind AS LastMessageKind,
               message.ResolutionState AS LastMessageResolutionState,
               message.Text AS LastMessageText,
               message.ReceivedUtc AS LastMessageUtc
        FROM Conversations AS conversation
        LEFT JOIN Messages AS message
          ON message.LocalSequence = (
              SELECT MAX(latest.LocalSequence)
              FROM Messages AS latest
              WHERE latest.ConversationId = conversation.Id)
        WHERE conversation.NodeId = $nodeId AND conversation.Kind = {kind}
        """;

    private static ConversationDirectoryEntry ReadEntry(
        SqliteDataReader result,
        ConversationDirectorySection section) => new(
        Guid.Parse(result.GetString(1)),
        result.GetString(0),
        section,
        (ConversationKind)result.GetInt32(2),
        ((byte[])result.GetValue(3)).ToArray(),
        result.IsDBNull(4) ? null : Guid.Parse(result.GetString(4)),
        result.IsDBNull(5) ? null : result.GetString(5),
        result.IsDBNull(6) ? null : result.GetInt32(6),
        result.IsDBNull(7) ? null : result.GetBoolean(7),
        result.IsDBNull(8) ? null : (ChannelAccessKind)result.GetInt32(8),
        ParseSlots(result.IsDBNull(9) ? null : result.GetString(9)),
        result.GetBoolean(10),
        ParseTimestamp(result.GetString(11)),
        result.GetInt64(12),
        ParseTimestamp(result.GetString(13)),
        result.IsDBNull(14) ? null : result.GetInt64(14),
        result.IsDBNull(15) ? null : (MessageDirection)result.GetInt32(15),
        result.IsDBNull(16) ? null : (StoredMessageKind)result.GetInt32(16),
        result.IsDBNull(17) ? null : (MessageResolutionState)result.GetInt32(17),
        result.IsDBNull(18) ? null : result.GetString(18),
        result.IsDBNull(19) ? null : ParseTimestamp(result.GetString(19)));

    private static IReadOnlyList<byte> ParseSlots(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        return value.Split(',').Select(byte.Parse).ToArray();
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void ValidateNodeId(Guid nodeId)
    {
        if (nodeId == Guid.Empty)
        {
            throw new ArgumentException("Node ID must not be empty.", nameof(nodeId));
        }
    }

    private static void ValidateIdentity(ReadOnlyMemory<byte> identity, string parameterName)
    {
        if (identity.Length != 32)
        {
            throw new ArgumentException("Identity must contain exactly 32 bytes.", parameterName);
        }
    }

    private static void ValidateSection(ConversationDirectorySection section)
    {
        if (!Enum.IsDefined(section))
        {
            throw new ArgumentOutOfRangeException(nameof(section));
        }
    }

    private static void ValidateCursor(
        ConversationDirectoryCursor? cursor,
        Guid nodeId,
        ConversationDirectorySection section)
    {
        if (cursor is not null &&
            (cursor.NodeId != nodeId ||
             cursor.Section != section ||
             cursor.ActivitySequence < 0 ||
             string.IsNullOrWhiteSpace(cursor.StableKey)))
        {
            throw new ArgumentException("Directory cursor is invalid.", nameof(cursor));
        }
    }

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"Page size must be between 1 and {MaximumPageSize}.");
        }
    }
}
