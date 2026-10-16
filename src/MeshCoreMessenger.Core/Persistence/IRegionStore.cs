using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence;

/// <summary>Node-scoped projections. Channel IDs use existing full-secret fingerprints, not slot/name/scope.</summary>
public interface IRegionReader
{
    Task<IReadOnlyList<RegionCatalogEntry>> GetRegionsAsync(Guid nodeId, bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<RegionCatalogEntry?> GetRegionAsync(Guid nodeId, Guid regionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChannelRegionPreference>> GetRegionAssignmentsAsync(Guid nodeId, Guid regionId, CancellationToken cancellationToken = default);
    /// <summary>Missing rows project as InheritDefault with revision zero.</summary>
    Task<ChannelRegionPreference> GetChannelPreferenceAsync(Guid nodeId, Guid channelId, CancellationToken cancellationToken = default);
    /// <summary>Persisted snapshots are returned as offline observations, never current-session evidence.</summary>
    Task<NodeRegionSnapshot?> GetNodeSnapshotAsync(Guid nodeId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RegionObservation>> GetObservationsAsync(Guid nodeId, CancellationToken cancellationToken = default);
}

/// <summary>Every mutation uses DatabaseWorker and publishes only after commit. No wire commands or deferred device writes.</summary>
public interface IRegionStore : IRegionReader
{
    event EventHandler<RegionDataCommit>? Changed;
    /// <summary>Deduplicate exact key identity within a node. Changing protocol name/key creates a new definition.</summary>
    Task<RegionCatalogEntry> AddRegionAsync(RegionCatalogEntry entry, CancellationToken cancellationToken = default);
    Task SetRegionLabelAsync(Guid nodeId, Guid regionId, string label, DateTimeOffset updatedUtc, CancellationToken cancellationToken = default);
    Task SetRegionArchivedAsync(Guid nodeId, Guid regionId, bool archived, DateTimeOffset updatedUtc, CancellationToken cancellationToken = default);
    /// <summary>Reject referenced regions; never silently reset assignments or delete attempt snapshots.</summary>
    Task DeleteRegionAsync(Guid nodeId, Guid regionId, CancellationToken cancellationToken = default);
    /// <summary>CAS on Revision; validate same-node channel/region and increment revision on commit.</summary>
    Task<ChannelRegionPreference> SetChannelPreferenceAsync(ChannelRegionPreference preference, CancellationToken cancellationToken = default);
    Task SaveNodeSnapshotAsync(NodeRegionSnapshot snapshot, CancellationToken cancellationToken = default);
    /// <summary>Upsert first/last evidence; bound retention without touching user catalogue.</summary>
    Task SaveObservationsAsync(IReadOnlyList<RegionObservation> observations, int maximumPerNode, DateTimeOffset retainSinceUtc, CancellationToken cancellationToken = default);
}

public sealed record RegionDataCommit(Guid NodeId, Guid? RegionId = null, Guid? ChannelId = null);

public sealed class RegionPreferenceConflictException() : InvalidOperationException("The channel region preference changed; reload before saving.");
