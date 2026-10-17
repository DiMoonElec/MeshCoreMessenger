namespace MeshCoreMessenger.Core.Domain;

public enum RegionDefaultChangeOutcome
{
    Applied,
    Rejected,
    CommandUncertain,
    ReadbackFailed,
    ReadbackMismatch,
    PersistenceFailed,
    SessionClosed,
}

/// <summary>No outcome authorizes automatic replay or rollback. Observed is the actual readback, if available.</summary>
public sealed record RegionDefaultChangeResult(RegionDefaultChangeOutcome Outcome, bool CommandAccepted,
    NodeRegionSnapshot? Observed = null, Exception? Error = null);
