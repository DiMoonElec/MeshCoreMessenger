using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Domain;

public sealed record RegionDiscoveryPreparationConsent(bool AllowContactCreation = false, bool AllowTemporaryZeroHop = false);
public enum RegionRoutePreparationOutcome { NotRequired, ContactCreationAllowed, ZeroHopPrepared, Rejected, StalePlan, Failed, Uncertain }
public enum RegionRouteRestorationOutcome { NotRequired, Restored, Conflict, Failed, SessionClosed, UnsafeToRestore }
public enum RegionContactCreationOutcome { NotApplicable, Observed, NotObserved, Unknown }

/// <summary>Remote response and local preparation/cleanup outcomes are independent. No field authorizes replay.</summary>
public sealed record RegionRouteRequestResult(RegionRoutePreparationOutcome Preparation, RegionRouteRestorationOutcome Restoration,
    RegionContactCreationOutcome ContactCreation, bool RequestInvoked, RegionNamesResponse? Response = null,
    Exception? Error = null, Exception? CleanupError = null);
