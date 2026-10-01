namespace MeshCoreMessenger.Core.Domain;

/// <summary>A stable node-scoped draft owner that may not have a conversation row yet.</summary>
public sealed record DraftTarget(
    Guid NodeId,
    Guid? ConversationId,
    ConversationKind Kind,
    byte[] Identity);
