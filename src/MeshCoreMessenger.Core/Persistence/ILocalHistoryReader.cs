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

    Task<HistoryMessagePosition?> GetMessagePositionAsync(
        Guid nodeId,
        Guid conversationId,
        Guid messageId,
        CancellationToken cancellationToken = default);

    Task<HistoryMessagePage> GetMessagesBeforeAsync(
        Guid nodeId,
        Guid conversationId,
        HistoryMessagePosition? before,
        int limit,
        CancellationToken cancellationToken = default);

    Task<HistoryMessagePage> GetMessagesAfterAsync(
        Guid nodeId,
        Guid conversationId,
        HistoryMessagePosition? after,
        int limit,
        CancellationToken cancellationToken = default);

    Task<HistoryMessagePage> GetMessagesAroundAsync(
        HistoryMessagePosition position,
        int beforeLimit,
        int afterLimit,
        CancellationToken cancellationToken = default);
}
