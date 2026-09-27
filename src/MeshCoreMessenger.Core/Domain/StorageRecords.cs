namespace MeshCoreMessenger.Core.Domain;

public sealed record NodeRecord(
    Guid Id,
    byte[] PublicKey,
    string? LastName,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc);

public sealed record SessionRecord(
    Guid Id,
    Guid ConnectionProfileId,
    Guid? NodeId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    string? EndReason);

public sealed record ContactRecord(
    Guid NodeId,
    byte[] PublicKey,
    byte[] PublicKeyPrefix,
    string DisplayName,
    int ContactType,
    int Flags,
    byte[]? OutPath,
    byte[]? AdvertPayload,
    bool PresentOnNode,
    DateTimeOffset? LastAdvertUtc,
    double? Latitude,
    double? Longitude,
    DateTimeOffset UpdatedUtc);

public sealed record ChannelRecord(
    Guid Id,
    Guid NodeId,
    string LastName,
    byte[] KeyFingerprint,
    ChannelAccessKind AccessKind,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record ChannelBindingRecord(
    Guid Id,
    Guid NodeId,
    byte Slot,
    int Generation,
    Guid ChannelId,
    DateTimeOffset BoundUtc,
    DateTimeOffset? UnboundUtc,
    Guid? ObservedSessionId);

public sealed record ConversationRecord(
    Guid Id,
    Guid NodeId,
    ConversationKind Kind,
    byte[]? ContactPublicKey,
    Guid? ChannelId,
    byte[]? UnknownIdentity,
    string? Title,
    bool IsArchived,
    long LastReadSequence,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record MessageRecord(
    Guid Id,
    long LocalSequence,
    Guid? EventId,
    Guid ConversationId,
    Guid? SessionId,
    MessageDirection Direction,
    StoredMessageKind MessageKind,
    int? TextType,
    byte? PathLength,
    ushort? BinaryDataType,
    string? Text,
    byte[]? Payload,
    byte[]? WirePayload,
    DateTimeOffset ReceivedUtc,
    long? WireTimestamp,
    byte[]? OriginalPublicKeyPrefix,
    byte[]? OriginalSenderPrefix,
    byte? OriginalChannelSlot,
    Guid? ChannelBindingId,
    MessageResolutionState ResolutionState,
    double? Snr,
    byte[]? Path);

public sealed record SendAttemptRecord(
    Guid Id,
    Guid MessageId,
    Guid? SessionId,
    int AttemptNumber,
    SendAttemptState State,
    DateTimeOffset StartedUtc,
    DateTimeOffset? AcceptedUtc,
    DateTimeOffset? CompletedUtc,
    long? WireTimestamp,
    byte[]? ExpectedAck,
    int? RoundTripMilliseconds,
    string? ErrorCode);

public sealed record DraftRecord(
    Guid ConversationId,
    string Text,
    DateTimeOffset UpdatedUtc);
