using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence;

/// <summary>Exact persisted, node-scoped message projection without identities or binary payloads.</summary>
public interface IMessageDetailsReader
{
    Task<CommittedMessageDetails?> GetAsync(Guid nodeId, Guid conversationId, Guid messageId,
        CancellationToken cancellationToken = default);
}

public sealed record CommittedMessageDetails(Guid NodeId, Guid ConversationId, Guid MessageId,
    long LocalSequence, ConversationKind ConversationKind, string? ConversationName,
    MessageDirection Direction, StoredMessageKind MessageKind, int? TextType, string? Text);
