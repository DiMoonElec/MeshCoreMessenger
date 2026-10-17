using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Application;

public sealed partial class RegionService
{
    public Task<RegionDefaultChangeResult> SetNodeDefaultAsync(SessionCommandOwner owner, RegionDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return ChangeNodeDefaultAsync(owner, definition, cancellationToken);
    }

    public Task<RegionDefaultChangeResult> ClearNodeDefaultAsync(SessionCommandOwner owner, CancellationToken cancellationToken = default) =>
        ChangeNodeDefaultAsync(owner, null, cancellationToken);

    private async Task<RegionDefaultChangeResult> ChangeNodeDefaultAsync(SessionCommandOwner owner, RegionDefinition? definition, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (commands is null) throw new InvalidOperationException("Online region configuration requires a session gateway.");
        await using var lease = commands.Acquire(owner.NodeId, new NodeCommandTarget(), token);
        if (lease.Owner.SessionId != owner.SessionId || lease.Owner.Generation != owner.Generation)
            throw new InvalidOperationException("The requested region owner is no longer active.");
        return await lease.ChangeNodeDefaultAsync(definition, store, timeProvider).ConfigureAwait(false);
    }
}
