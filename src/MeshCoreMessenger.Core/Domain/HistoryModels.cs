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
    DateTimeOffset ReceivedUtc)
{
    /// <summary>Persisted wire text subtype; unknown values are preserved.</summary>
    public int? TextType { get; init; }

    /// <summary>Persisted binary subtype without exporting the payload.</summary>
    public ushort? BinaryDataType { get; init; }

    /// <summary>Device-provided timestamp when one existed; display order never depends on it.</summary>
    public long? WireTimestamp { get; init; }

    public MessageResolutionState ResolutionState { get; init; }
}

/// <summary>An exact, node-scoped location of one persisted message.</summary>
public sealed record HistoryMessagePosition(
    Guid NodeId,
    Guid ConversationId,
    Guid MessageId,
    long LocalSequence);

/// <summary>A bounded chronological page plus continuation information in both directions.</summary>
public sealed record HistoryMessagePage(
    IReadOnlyList<HistoryMessage> Items,
    HistoryMessagePosition? FirstPosition,
    HistoryMessagePosition? LastPosition,
    bool HasEarlier,
    bool HasLater);
