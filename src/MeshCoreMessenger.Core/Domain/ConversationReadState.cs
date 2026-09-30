namespace MeshCoreMessenger.Core.Domain;

/// <summary>Persisted, node-scoped read watermark and its incoming-message projection.</summary>
public sealed record ConversationReadState(
    Guid NodeId,
    Guid ConversationId,
    long LastReadSequence,
    long UnreadCount,
    HistoryMessagePosition? FirstUnreadPosition);
