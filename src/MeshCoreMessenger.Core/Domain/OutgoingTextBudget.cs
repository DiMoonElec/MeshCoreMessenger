namespace MeshCoreMessenger.Core.Domain;

/// <summary>A presentation estimate, not send admission. Send resolves and validates again.
/// Offline observations intentionally have no transport byte limit.</summary>
public sealed record OutgoingTextBudget(Guid NodeId, Guid? SessionId, long? Generation, OutgoingRecipient Recipient,
    ResolvedRegionScope? Scope, int? MaximumTextBytes, RegionSnapshotFreshness Freshness, long? DefaultRevision, string? SenderName)
{
    public RegionSupport ScopeSupport { get; init; } = RegionSupport.Unknown;
}
