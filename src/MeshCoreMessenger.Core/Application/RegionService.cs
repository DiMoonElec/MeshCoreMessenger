using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Application;

/// <summary>Local region catalogue and channel policy owner. Offline changes never enqueue device commands.</summary>
public sealed partial class RegionService(IRegionStore store, TimeProvider timeProvider, ISessionCommandGateway? commands = null,
    IRegionDiscoveryService? discovery = null) : IRegionService
{
    public Task<RegionDiscoveryRunResult> DiscoverAsync(SessionCommandOwner owner, RegionDiscoveryOptions? options = null,
        IProgress<RegionDiscoveryProgress>? progress = null, CancellationToken cancellationToken = default) =>
        (discovery ?? throw new InvalidOperationException("Region discovery is unavailable in this offline service.")).DiscoverAsync(owner, options, progress, cancellationToken);
    public event EventHandler<RegionDataCommit>? Changed { add => store.Changed += value; remove => store.Changed -= value; }
    public Task<IReadOnlyList<RegionCatalogEntry>> GetRegionsAsync(Guid nodeId, bool includeArchived = false, CancellationToken cancellationToken = default) =>
        store.GetRegionsAsync(nodeId, includeArchived, cancellationToken);
    public Task<ChannelRegionPreference> GetChannelPreferenceAsync(Guid nodeId, Guid channelId, CancellationToken cancellationToken = default) =>
        store.GetChannelPreferenceAsync(nodeId, channelId, cancellationToken);
    public Task<RegionCatalogEntry> CreatePublicAsync(Guid nodeId, string label, string protocolName, CancellationToken cancellationToken = default) =>
        AddAsync(nodeId, label, RegionDefinition.Public(protocolName), cancellationToken);
    public Task<RegionCatalogEntry> ImportCustomAsync(Guid nodeId, string label, string protocolName, ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken = default) => AddAsync(nodeId, label, RegionDefinition.Custom(protocolName, new RegionKey(key.Span)), cancellationToken);
    private Task<RegionCatalogEntry> AddAsync(Guid nodeId, string label, RegionDefinition definition, CancellationToken token)
    {
        var now = timeProvider.GetUtcNow();
        return store.AddRegionAsync(new(Guid.NewGuid(), nodeId, label, definition, RegionProvenance.Manual, now, now), token);
    }
    public Task RenameLabelAsync(Guid nodeId, Guid regionId, string label, CancellationToken cancellationToken = default) =>
        store.SetRegionLabelAsync(nodeId, regionId, label, timeProvider.GetUtcNow(), cancellationToken);
    public Task SetArchivedAsync(Guid nodeId, Guid regionId, bool archived, CancellationToken cancellationToken = default) =>
        store.SetRegionArchivedAsync(nodeId, regionId, archived, timeProvider.GetUtcNow(), cancellationToken);
    public Task DeleteAsync(Guid nodeId, Guid regionId, CancellationToken cancellationToken = default) =>
        store.DeleteRegionAsync(nodeId, regionId, cancellationToken);
    public Task<ChannelRegionPreference> AssignChannelAsync(Guid nodeId, Guid channelId, FloodScopeMode mode, Guid? regionId,
        long expectedRevision, CancellationToken cancellationToken = default) =>
        store.SetChannelPreferenceAsync(new(nodeId, channelId, mode, regionId, expectedRevision), cancellationToken);

    /// <summary>Creates a new definition without rewriting assignments or historical captures.</summary>
    public async Task<RegionCatalogEntry> CreateReplacementAsync(Guid nodeId, Guid regionId, RegionDefinition replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var previous = await store.GetRegionAsync(nodeId, regionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Region does not belong to this node.");
        if (replacement.Key.Equals(previous.Definition.Key))
        {
            var sameName = replacement.Name == previous.ProtocolName ||
                replacement.Kind == RegionKeyKind.PublicDerived && previous.Definition.Kind == RegionKeyKind.PublicDerived &&
                RegionKeys.NormalizePublicName(replacement.Name) == RegionKeys.NormalizePublicName(previous.ProtocolName);
            if (sameName) return previous;
            throw new ArgumentException("A replacement protocol definition requires a new key; use display-label rename for the same identity.", nameof(replacement));
        }
        return await AddAsync(nodeId, previous.Label, replacement, cancellationToken).ConfigureAwait(false);
    }
}
