using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence;

/// <summary>Reads bounded pages of persisted conversations and messages.</summary>
public interface ILocalHistoryReader
{
    Task<IReadOnlyList<ConversationSummary>> GetConversationsAsync(
        Guid nodeId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HistoryMessage>> GetMessagesAsync(
        Guid nodeId,
        Guid conversationId,
        long? beforeLocalSequence,
        int limit,
        CancellationToken cancellationToken = default);
}
