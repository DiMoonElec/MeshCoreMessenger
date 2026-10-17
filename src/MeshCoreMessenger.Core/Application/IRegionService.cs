using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Application;

public interface IRegionService
{
    event EventHandler<RegionDataCommit>? Changed;
    Task<RegionDiscoveryRunResult> DiscoverAsync(SessionCommandOwner owner, RegionDiscoveryOptions? options = null,
        IProgress<RegionDiscoveryProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<RegionDefaultChangeResult> SetNodeDefaultAsync(SessionCommandOwner owner, RegionDefinition definition, CancellationToken cancellationToken = default);
    Task<RegionDefaultChangeResult> ClearNodeDefaultAsync(SessionCommandOwner owner, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RegionCatalogEntry>> GetRegionsAsync(Guid nodeId, bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<ChannelRegionPreference> GetChannelPreferenceAsync(Guid nodeId, Guid channelId, CancellationToken cancellationToken = default);
    Task<RegionCatalogEntry> CreatePublicAsync(Guid nodeId, string label, string protocolName, CancellationToken cancellationToken = default);
    Task<RegionCatalogEntry> ImportCustomAsync(Guid nodeId, string label, string protocolName, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);
    Task<RegionCatalogEntry> CreateReplacementAsync(Guid nodeId, Guid regionId, RegionDefinition replacement, CancellationToken cancellationToken = default);
    Task RenameLabelAsync(Guid nodeId, Guid regionId, string label, CancellationToken cancellationToken = default);
    Task SetArchivedAsync(Guid nodeId, Guid regionId, bool archived, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid nodeId, Guid regionId, CancellationToken cancellationToken = default);
    Task<ChannelRegionPreference> AssignChannelAsync(Guid nodeId, Guid channelId, FloodScopeMode mode, Guid? regionId, long expectedRevision, CancellationToken cancellationToken = default);
}
