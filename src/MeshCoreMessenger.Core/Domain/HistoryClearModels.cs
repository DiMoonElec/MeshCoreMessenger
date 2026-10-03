namespace MeshCoreMessenger.Core.Domain;

public sealed record HistoryClearStatus(Guid NodeId, Guid ConversationId, ConversationKind Kind,
    byte[] Identity, long MessageCount, bool HasPendingSend);
public sealed record HistoryClearResult(Guid NodeId, Guid ConversationId, long CutoffSequence, int DeletedCount);
