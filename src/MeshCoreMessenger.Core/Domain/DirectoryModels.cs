namespace MeshCoreMessenger.Core.Domain;

/// <summary>Secret-free, normalized directory data ready for local persistence.</summary>
public sealed record DirectoryContactSnapshot(
    byte[] PublicKey,
    string DisplayName,
    int ContactType,
    int Flags,
    byte[] OutPath,
    DateTimeOffset LastAdvertUtc,
    double Latitude,
    double Longitude);

/// <summary>One non-empty Companion channel slot. KeyFingerprint is SHA-256, never the secret.</summary>
public sealed record DirectoryChannelSnapshot(
    byte Slot,
    string Name,
    byte[] KeyFingerprint,
    ChannelAccessKind AccessKind);

public enum ChannelTransitionKind
{
    Rebind,
    Remove,
}

/// <summary>
/// A deferred change to an active channel slot. B5 commits it only after the initial message drain.
/// </summary>
public sealed record PendingChannelTransition(
    Guid NodeId,
    Guid SessionId,
    byte Slot,
    ChannelTransitionKind Kind,
    ChannelBindingRecord PreviousBinding,
    ChannelRecord? NextChannel);

public sealed record DirectorySnapshotResult(
    Guid NodeId,
    Guid SessionId,
    IReadOnlyList<ContactRecord> CurrentContacts,
    IReadOnlyList<ChannelBindingRecord> ActiveBindings,
    IReadOnlyList<PendingChannelTransition> PendingChannelTransitions);
