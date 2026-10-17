using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Application;

public sealed partial class MessageService
{
    public async Task<OutgoingTextBudget> GetTextBudgetAsync(Guid nodeId, OutgoingRecipient recipient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        var captured = recipient with { Identity = recipient.Identity.ToArray() };
        SessionCommandTarget target = captured.Kind switch
        {
            ConversationKind.Channel => new ChannelCommandTarget(captured),
            ConversationKind.Contact => new ContactCommandTarget(captured.Identity),
            _ => throw new ArgumentException("A resolved text recipient is required.", nameof(recipient)),
        };
        SessionCommandLease lease;
        try { lease = gateway.Acquire(nodeId, target, cancellationToken); }
        catch (InvalidOperationException)
        {
            return new(nodeId, null, null, captured, null, null, RegionSnapshotFreshness.Offline, null, null);
        }
        await using (lease.ConfigureAwait(false))
        {
            return await lease.RunAsync(async (owned, token) =>
            {
                await owned.ValidateRecipientAsync().ConfigureAwait(false);
                long defaultRevision = 0;
                var scope = captured.Kind == ConversationKind.Channel && regions is not null
                    ? await owned.ResolveChannelScopeAsync(regions, directories, owned.MaximumTextBytes, revision => defaultRevision = revision).ConfigureAwait(false)
                    : await owned.ResolveDefaultScopeAsync(owned.MaximumTextBytes, revision => defaultRevision = revision).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return new OutgoingTextBudget(nodeId, owned.Owner.SessionId, owned.Owner.Generation, captured, scope, scope.MaximumTextBytes,
                    RegionSnapshotFreshness.CurrentSession, defaultRevision, owned.Owner.SenderName)
                {
                    ScopeSupport = scope.RequestedMode switch
                    {
                        FloodScopeMode.InheritDefault => RegionSupport.Supported,
                        FloodScopeMode.ForceUnscoped => owned.Owner.RegionCapabilities?.ForceUnscoped ?? RegionSupport.Unknown,
                        _ => owned.Owner.RegionCapabilities?.TemporaryScope ?? RegionSupport.Unknown,
                    },
                };
            }).ConfigureAwait(false);
        }
    }
}
