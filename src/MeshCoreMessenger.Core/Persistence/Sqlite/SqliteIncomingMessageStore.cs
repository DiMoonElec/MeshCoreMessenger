using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed class SqliteIncomingMessageStore(DatabaseWorker writer) : IIncomingMessageStore
{
    public Task<StoredIncomingMessage> StoreAsync(IncomingMessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        Validate(envelope);
        return writer.ExecuteAsync(connection => Store(connection, envelope), cancellationToken);
    }

    private static StoredIncomingMessage Store(SqliteConnection connection, IncomingMessageEnvelope envelope)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            var existing = TryReadExisting(connection, transaction, envelope.EventId);
            if (existing is not null)
            {
                transaction.Commit();
                return existing;
            }
            var resolution = ResolveConversation(connection, transaction, envelope);
            var data = MessageData.From(envelope.Message);
            var messageId = Guid.NewGuid();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO Messages (
                    Id, EventId, ConversationId, SessionId, Direction, MessageKind, TextType, PathLength,
                    BinaryDataType, Text, Payload, WirePayload, ReceivedUtc, WireTimestamp,
                    OriginalPublicKeyPrefix, OriginalSenderPrefix, OriginalChannelSlot, ChannelBindingId,
                    ResolutionState, Snr, Path)
                VALUES (
                    $id, $eventId, $conversationId, $sessionId, 0, $messageKind, $textType, $pathLength,
                    $binaryDataType, $text, $payload, NULL, $receivedUtc, $wireTimestamp,
                    $publicPrefix, $senderPrefix, $channelSlot, $channelBindingId,
                    $resolutionState, $snr, NULL)
                ON CONFLICT(EventId) DO NOTHING;
                """;
            Add(insert, "$id", messageId);
            Add(insert, "$eventId", envelope.EventId);
            Add(insert, "$conversationId", resolution.ConversationId);
            Add(insert, "$sessionId", envelope.SessionId);
            insert.Parameters.AddWithValue("$messageKind", (int)data.Kind);
            Add(insert, "$textType", data.TextType);
            Add(insert, "$pathLength", data.PathLength);
            Add(insert, "$binaryDataType", data.BinaryDataType);
            Add(insert, "$text", data.Text);
            AddBlob(insert, "$payload", data.Payload);
            insert.Parameters.AddWithValue("$receivedUtc", envelope.ReceivedUtc.ToString("O"));
            Add(insert, "$wireTimestamp", data.WireTimestamp);
            AddBlob(insert, "$publicPrefix", data.PublicKeyPrefix);
            AddBlob(insert, "$senderPrefix", data.SenderPrefix);
            Add(insert, "$channelSlot", data.ChannelSlot);
            Add(insert, "$channelBindingId", resolution.ChannelBindingId);
            insert.Parameters.AddWithValue("$resolutionState", (int)resolution.State);
            Add(insert, "$snr", data.Snr);
            var inserted = insert.ExecuteNonQuery() == 1;
            if (!inserted)
            {
                existing = TryReadExisting(connection, transaction, envelope.EventId)
                    ?? throw new InvalidOperationException("Existing incoming event was not found.");
                transaction.Commit();
                return existing;
            }

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE Conversations SET UpdatedUtc = $updatedUtc WHERE Id = $id;";
                Add(update, "$id", resolution.ConversationId);
                update.Parameters.AddWithValue("$updatedUtc", envelope.ReceivedUtc.ToString("O"));
                update.ExecuteNonQuery();
            }

            long sequence;
            using (var lastInsert = connection.CreateCommand())
            {
                lastInsert.Transaction = transaction;
                lastInsert.CommandText = "SELECT last_insert_rowid();";
                sequence = (long)lastInsert.ExecuteScalar()!;
            }
            transaction.Commit();
            return new StoredIncomingMessage(messageId, envelope.EventId, resolution.ConversationId, sequence, true);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static Resolution ResolveConversation(SqliteConnection connection, SqliteTransaction transaction, IncomingMessageEnvelope envelope)
    {
        return envelope.Message switch
        {
            ContactMessage contact => ResolveContact(connection, transaction, envelope, contact),
            ChannelMessage channel => ResolveChannel(connection, transaction, envelope, channel.ChannelIndex),
            ChannelDataMessage channel => ResolveChannel(connection, transaction, envelope, channel.ChannelIndex),
            _ => throw new NotSupportedException($"Unsupported incoming message type '{envelope.Message.GetType().Name}'."),
        };
    }

    private static Resolution ResolveContact(SqliteConnection connection, SqliteTransaction transaction, IncomingMessageEnvelope envelope, ContactMessage message)
    {
        var prefix = message.ContactPublicKeyPrefix.ToArray();
        var keys = new List<byte[]>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT PublicKey FROM Contacts WHERE NodeId = $nodeId AND PublicKeyPrefix = $prefix AND PresentOnNode = 1;";
            Add(command, "$nodeId", envelope.NodeId);
            AddBlob(command, "$prefix", prefix);
            using var result = command.ExecuteReader();
            while (result.Read()) keys.Add((byte[])result.GetValue(0));
        }

        if (keys.Count == 1)
        {
            var conversation = FindOrCreateConversation(connection, transaction, envelope.NodeId, ConversationKind.Contact, keys[0], null, null, null, envelope.ReceivedUtc);
            return new Resolution(conversation, MessageResolutionState.Resolved, null);
        }

        var identity = new byte[7];
        identity[0] = keys.Count == 0 ? (byte)0 : (byte)1;
        prefix.CopyTo(identity, 1);
        var unknown = FindOrCreateConversation(connection, transaction, envelope.NodeId, ConversationKind.UnknownContact, null, null, identity, "Unknown contact", envelope.ReceivedUtc);
        return new Resolution(unknown, keys.Count == 0 ? MessageResolutionState.Unresolved : MessageResolutionState.Ambiguous, null);
    }

    private static Resolution ResolveChannel(SqliteConnection connection, SqliteTransaction transaction, IncomingMessageEnvelope envelope, byte slot)
    {
        if (envelope.StableChannelBinding is { } binding)
        {
            if (binding.NodeId != envelope.NodeId || binding.Slot != slot || binding.UnboundUtc is not null)
                throw new ArgumentException("Stable channel binding does not match the incoming message.", nameof(envelope));
            if (!IsActiveBinding(connection, transaction, binding))
                throw new ArgumentException("Stable channel binding is not present in local storage.", nameof(envelope));
            var conversation = FindOrCreateConversation(connection, transaction, envelope.NodeId, ConversationKind.Channel, null, binding.ChannelId, null, null, envelope.ReceivedUtc);
            return new Resolution(conversation, MessageResolutionState.Resolved, binding.Id);
        }

        var identity = envelope.UnknownChannelIdentity?.ToArray() ?? BuildUnknownChannelIdentity(envelope.SessionId, slot);
        var unknown = FindOrCreateConversation(connection, transaction, envelope.NodeId, ConversationKind.UnknownChannel, null, null, identity, "Unknown channel", envelope.ReceivedUtc);
        return new Resolution(unknown, MessageResolutionState.Unresolved, null);
    }

    private static Guid FindOrCreateConversation(SqliteConnection connection, SqliteTransaction transaction, Guid nodeId, ConversationKind kind, byte[]? contactKey, Guid? channelId, byte[]? unknownIdentity, string? title, DateTimeOffset now)
    {
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = kind switch
            {
                ConversationKind.Contact => "SELECT Id FROM Conversations WHERE NodeId = $nodeId AND Kind = 0 AND ContactPublicKey = $contactKey;",
                ConversationKind.Channel => "SELECT Id FROM Conversations WHERE NodeId = $nodeId AND Kind = 1 AND ChannelId = $channelId;",
                _ => "SELECT Id FROM Conversations WHERE NodeId = $nodeId AND Kind = $kind AND UnknownIdentity = $unknownIdentity;",
            };
            Add(find, "$nodeId", nodeId); Add(find, "$contactKey", contactKey); Add(find, "$channelId", channelId); Add(find, "$kind", (int)kind); AddBlob(find, "$unknownIdentity", unknownIdentity);
            if (find.ExecuteScalar() is string id) return Guid.Parse(id);
        }
        var idNew = Guid.NewGuid();
        using var create = connection.CreateCommand(); create.Transaction = transaction;
        create.CommandText = "INSERT INTO Conversations (Id, NodeId, Kind, ContactPublicKey, ChannelId, UnknownIdentity, Title, IsArchived, LastReadSequence, CreatedUtc, UpdatedUtc) VALUES ($id, $nodeId, $kind, $contactKey, $channelId, $unknownIdentity, $title, 0, 0, $now, $now);";
        Add(create, "$id", idNew); Add(create, "$nodeId", nodeId); Add(create, "$kind", (int)kind); AddBlob(create, "$contactKey", contactKey); Add(create, "$channelId", channelId); AddBlob(create, "$unknownIdentity", unknownIdentity); Add(create, "$title", title); create.Parameters.AddWithValue("$now", now.ToString("O")); create.ExecuteNonQuery();
        return idNew;
    }

    private static StoredIncomingMessage? TryReadExisting(SqliteConnection connection, SqliteTransaction transaction, Guid eventId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT Id, ConversationId, LocalSequence FROM Messages WHERE EventId = $eventId;"; Add(command, "$eventId", eventId);
        using var result = command.ExecuteReader();
        if (!result.Read()) return null;
        return new StoredIncomingMessage(Guid.Parse(result.GetString(0)), eventId, Guid.Parse(result.GetString(1)), result.GetInt64(2), false);
    }

    private static bool IsActiveBinding(SqliteConnection connection, SqliteTransaction transaction, ChannelBindingRecord binding)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM ChannelBindings WHERE Id = $id AND NodeId = $nodeId AND Slot = $slot AND ChannelId = $channelId AND UnboundUtc IS NULL;";
        Add(command, "$id", binding.Id); Add(command, "$nodeId", binding.NodeId); Add(command, "$slot", binding.Slot); Add(command, "$channelId", binding.ChannelId);
        return command.ExecuteScalar() is not null;
    }

    private static byte[] BuildUnknownChannelIdentity(Guid sessionId, byte slot) => sessionId.ToByteArray().Append(slot).ToArray();
    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value is Guid guid ? guid.ToString("D") : value ?? DBNull.Value);
    private static void AddBlob(SqliteCommand command, string name, byte[]? value) => command.Parameters.Add(name, SqliteType.Blob).Value = (object?)value ?? DBNull.Value;

    private static void Validate(IncomingMessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope); ArgumentNullException.ThrowIfNull(envelope.Message);
        if (envelope.EventId == Guid.Empty || envelope.SessionId == Guid.Empty || envelope.NodeId == Guid.Empty) throw new ArgumentException("Incoming message IDs must not be empty.", nameof(envelope));
        if (envelope.UnknownChannelIdentity is { Length: 0 }) throw new ArgumentException("Unknown channel identity must not be empty.", nameof(envelope));
        if (envelope.Message is ContactMessage contact && contact.ContactPublicKeyPrefix.Length != 6)
            throw new ArgumentException("Private-message contact prefix must be six bytes.", nameof(envelope));
        if (envelope.Message is ContactMessage { SenderPrefix.Length: > 0 and not 4 })
            throw new ArgumentException("Private-message sender prefix must be empty or four bytes.", nameof(envelope));
    }

    private sealed record Resolution(Guid ConversationId, MessageResolutionState State, Guid? ChannelBindingId);
    private sealed record MessageData(StoredMessageKind Kind, int? TextType, byte PathLength, ushort? BinaryDataType, string? Text, byte[]? Payload, long? WireTimestamp, byte[]? PublicKeyPrefix, byte[]? SenderPrefix, byte? ChannelSlot, double? Snr)
    {
        public static MessageData From(ReceivedMessage message) => message switch
        {
            ContactMessage value => new(StoredMessageKind.Text, (int)value.TextType, value.PathLength, null, value.Text, null, value.Timestamp.ToUnixTimeSeconds(), value.ContactPublicKeyPrefix.ToArray(), value.SenderPrefix.IsEmpty ? null : value.SenderPrefix.ToArray(), null, value.SnrDb),
            ChannelMessage value => new(StoredMessageKind.Text, (int)value.TextType, value.PathLength, null, value.Text, null, value.Timestamp.ToUnixTimeSeconds(), null, null, value.ChannelIndex, value.SnrDb),
            ChannelDataMessage value => new(StoredMessageKind.Binary, null, value.PathLength, value.DataType, null, value.Data.ToArray(), null, null, null, value.ChannelIndex, value.SnrDb),
            _ => throw new NotSupportedException($"Unsupported incoming message type '{message.GetType().Name}'."),
        };
    }
}
