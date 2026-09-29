namespace MeshCoreMessenger.Core.Domain;

public enum ConversationDirectorySection
{
    ChatContacts = 0,
    ServiceContacts = 1,
    Channels = 2,
    UnknownContacts = 3,
    UnknownChannels = 4,
}

/// <summary>Stable cursor for one unchanged directory section.</summary>
public sealed record ConversationDirectoryCursor(
    Guid NodeId,
    ConversationDirectorySection Section,
    long ActivitySequence,
    DateTimeOffset ActivityUtc,
    string StableKey);

/// <summary>Secret-free row used by bounded conversation and directory lists.</summary>
public sealed record ConversationDirectoryEntry(
    Guid NodeId,
    string StableKey,
    ConversationDirectorySection Section,
    ConversationKind Kind,
    byte[] Identity,
    Guid? ConversationId,
    string? DisplayName,
    int? ContactType,
    bool? PresentOnNode,
    ChannelAccessKind? ChannelAccessKind,
    IReadOnlyList<byte> ActiveChannelSlots,
    bool IsArchived,
    DateTimeOffset UpdatedUtc,
    long ActivitySequence,
    DateTimeOffset ActivityUtc,
    long? LastMessageSequence,
    MessageDirection? LastMessageDirection,
    StoredMessageKind? LastMessageKind,
    MessageResolutionState? LastMessageResolutionState,
    string? LastMessageText,
    DateTimeOffset? LastMessageUtc);

public sealed record ConversationDirectoryPage(
    IReadOnlyList<ConversationDirectoryEntry> Items,
    ConversationDirectoryCursor? NextCursor);

/// <summary>Committed contact details. Byte arrays are defensive copies.</summary>
public sealed record ContactDetailsProjection(
    Guid NodeId,
    byte[] PublicKey,
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

/// <summary>Committed channel metadata without the channel secret.</summary>
public sealed record ChannelDetailsProjection(
    Guid Id,
    Guid NodeId,
    string DisplayName,
    byte[] KeyFingerprint,
    ChannelAccessKind AccessKind,
    IReadOnlyList<byte> ActiveSlots,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
