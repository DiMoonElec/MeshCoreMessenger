namespace MeshCoreMessenger.Core.Domain;

/// <summary>A read-only conversation projection for the offline desktop shell.</summary>
public sealed record ConversationSummary(
    Guid Id,
    Guid NodeId,
    ConversationKind Kind,
    string? Title,
    bool IsArchived,
    DateTimeOffset UpdatedUtc,
    long? LastMessageSequence,
    MessageDirection? LastMessageDirection,
    StoredMessageKind? LastMessageKind,
    string? LastMessageText,
    DateTimeOffset? LastMessageUtc);

/// <summary>A read-only message projection for the offline desktop shell.</summary>
public sealed record HistoryMessage(
    Guid Id,
    long LocalSequence,
    Guid ConversationId,
    MessageDirection Direction,
    StoredMessageKind MessageKind,
    string? Text,
    DateTimeOffset ReceivedUtc);
