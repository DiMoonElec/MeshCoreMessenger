using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Domain;

public enum RegionDiscoveryTargetStatus
{
    SuccessEmpty, SuccessPossiblyPartial, NoDirectPath, Unsupported, Timeout, Cancelled, Malformed, ContactTableFull,
    PreparationRequired, StalePlan, PreparationFailed, StorageFailed, TransportFailed, SuccessWithRouteWarning,
}
public enum DiscoveredRegionNameKind { Public, PrivateKeyRequired, Unscoped, Unrecognized }
public enum RegionDiscoveryPhase { Scanning, Preparing, Requesting, Completed }

/// <summary>Terminal evidence for one full-key target. Names are observations, never region-key identities.</summary>
public sealed record RegionDiscoveryTargetOutcome(Guid RunId, Guid NodeId, Guid SessionId, long Generation,
    string RepeaterPublicKeyHex, DateTimeOffset CompletedUtc, RegionDiscoveryTargetStatus Status, IReadOnlyList<string> Names,
    RegionRoutePreparationOutcome Preparation = RegionRoutePreparationOutcome.NotRequired,
    RegionRouteRestorationOutcome Restoration = RegionRouteRestorationOutcome.NotRequired,
    RegionContactCreationOutcome ContactCreation = RegionContactCreationOutcome.NotApplicable, string? ErrorCode = null)
{
    public bool Persisted { get; init; }
    public bool HasRouteWarning => Restoration is not (RegionRouteRestorationOutcome.NotRequired or RegionRouteRestorationOutcome.Restored);
}
public sealed record DiscoveredRegionName(string ExactName, DiscoveredRegionNameKind Kind, IReadOnlyList<string> SourcePublicKeys);
public sealed record RegionDiscoveryProgress(Guid RunId, RegionDiscoveryPhase Phase, int Completed, int Total, string? TargetPublicKeyHex = null,
    IReadOnlyList<DiscoveredNeighbor>? Neighbors = null, IReadOnlyList<DiscoveredRegionName>? Names = null,
    RegionDiscoveryOwner? Owner = null);
public sealed record RegionDiscoveryOwner(Guid NodeId, Guid SessionId, long Generation);
public sealed record RegionDiscoveryRunResult(Guid RunId, RegionDiscoveryOwner Owner, IReadOnlyList<RegionDiscoveryPlan> Plans,
    IReadOnlyList<RegionDiscoveryTargetOutcome> Outcomes, IReadOnlyList<DiscoveredRegionName> Names,
    bool Cancelled, bool DeadlineReached, bool SessionEnded, bool NeighborCapacityReached, int OmittedTargets, Exception? Error = null)
{
    public bool IsPotentiallyIncomplete => true;
    public int UnresolvedPrefixCount { get; init; }
}

public sealed record RegionDiscoveryOptions
{
    public NeighborDiscoveryOptions Neighbors { get; init; } = new();
    public int MaximumTargets { get; init; } = 16;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan? TotalTimeout { get; init; } = TimeSpan.FromMinutes(1);
    public RegionDiscoveryPreparationConsent Consent { get; init; } = new();
    public IReadOnlyList<string>? SelectedTargetPublicKeys { get; init; }
    public IReadOnlyList<ConfirmedRegionReplyPath> ConfirmedReplyPaths { get; init; } = [];
    public int MaximumStoredObservations { get; init; } = 1024;
    public int MaximumStoredOutcomes { get; init; } = 512;
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(30);
    public bool OnlyDiscoveredNeighbors { get; init; }
    public int MaximumAttempts { get; init; } = 1;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    public static RegionDiscoveryOptions DirectNeighbors() => new()
    {
        OnlyDiscoveredNeighbors = true, MaximumTargets = 4, MaximumAttempts = 3,
        Neighbors = new() { CollectionWindow = TimeSpan.FromSeconds(30) },
        RequestTimeout = TimeSpan.FromSeconds(15), TotalTimeout = null,
        Consent = new(true, true)
    };
}
