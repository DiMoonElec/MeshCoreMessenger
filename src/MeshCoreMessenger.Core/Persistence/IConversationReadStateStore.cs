using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence;

/// <summary>Reads and monotonically advances persisted conversation read watermarks.</summary>
public interface IConversationReadStateStore
{
    Task<ConversationReadState> GetAsync(
        Guid nodeId,
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<ConversationReadState> AdvanceAsync(
        HistoryMessagePosition through,
        CancellationToken cancellationToken = default);
}
