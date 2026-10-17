using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Application;

public sealed partial class SessionCommandLease
{
    internal Task<RegionDefaultChangeResult> ChangeNodeDefaultAsync(RegionDefinition? definition, IRegionStore store, TimeProvider clock) =>
        OwnAsync(() => _scope.WithRegionStateAsync(this, async () =>
        {
            if (Target is not NodeCommandTarget) throw new InvalidOperationException("Default configuration requires a node target.");
            var previous = _scope.RegionSnapshot;
            var support = _scope.Capabilities?.DefaultReadWrite ?? RegionSupport.Unknown;
            if (support == RegionSupport.Unsupported)
                return new RegionDefaultChangeResult(RegionDefaultChangeOutcome.Rejected, false, Error: new NotSupportedException("Default configuration is unsupported."));
            var revision = previous?.Revision ?? 0;
            NodeRegionSnapshot Unknown() => new(Owner.NodeId, Owner.SessionId, Owner.Generation, clock.GetUtcNow(),
                support, NodeFloodScope.Unknown, RegionSnapshotFreshness.Unknown, revision);
            var uncertain = Unknown();
            var commandStarted = false;
            try
            {
                await _scope.Invoke(this, async (client, token) =>
                {
                    if (!_scope.PublishRegion(uncertain)) throw new InvalidOperationException("Region owner is no longer active.");
                    commandStarted = true;
                    if (definition is null) await client.ClearDefaultFloodScopeAsync(token).ConfigureAwait(false);
                    else await client.SetDefaultFloodScopeAsync(definition, token).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
            }
            catch (Exception error) when (error is MeshCoreCommandException or NotSupportedException or ArgumentException)
            {
                if (previous is not null) _scope.PublishRegion(previous);
                return new RegionDefaultChangeResult(RegionDefaultChangeOutcome.Rejected, false, Error: error);
            }
            catch (Exception error)
            {
                if (!commandStarted) throw;
                return await SaveUncertainAsync(RegionDefaultChangeOutcome.CommandUncertain, false, error).ConfigureAwait(false);
            }

            NodeRegionSnapshot observed;
            try
            {
                var value = await _scope.Invoke(this, (client, token) => client.GetDefaultFloodScopeAsync(token)).ConfigureAwait(false);
                Token.ThrowIfCancellationRequested();
                if (value.State == NodeFloodScopeState.Unknown) throw new InvalidOperationException("Readback did not establish the default.");
                observed = new(Owner.NodeId, Owner.SessionId, Owner.Generation, clock.GetUtcNow(), RegionSupport.Supported,
                    value, RegionSnapshotFreshness.CurrentSession, checked(revision + 1));
            }
            catch (Exception error)
            {
                return await SaveUncertainAsync(RegionDefaultChangeOutcome.ReadbackFailed, true, error).ConfigureAwait(false);
            }
            try { await store.SaveNodeSnapshotAsync(observed, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error)
            {
                _scope.Outgoing.ReportFailure(error);
                return new RegionDefaultChangeResult(RegionDefaultChangeOutcome.PersistenceFailed, true, observed, error);
            }
            if (!_scope.PublishRegion(observed)) return new RegionDefaultChangeResult(RegionDefaultChangeOutcome.SessionClosed, true, observed);
            var matches = definition is null ? observed.Value.State == NodeFloodScopeState.KnownEmpty :
                observed.Value.State == NodeFloodScopeState.Configured && observed.Value.Name == definition.Name && observed.Value.Key!.Equals(definition.Key);
            return new RegionDefaultChangeResult(matches ? RegionDefaultChangeOutcome.Applied : RegionDefaultChangeOutcome.ReadbackMismatch, true, observed);

            async Task<RegionDefaultChangeResult> SaveUncertainAsync(RegionDefaultChangeOutcome outcome, bool accepted, Exception error)
            {
                // The owned workflow remains part of session drain even after caller cancellation.
                // Preserve uncertainty durably without issuing another command or restoring stale preferences.
                try { await store.SaveNodeSnapshotAsync(Unknown(), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception storageError)
                {
                    _scope.Outgoing.ReportFailure(storageError);
                    return new(RegionDefaultChangeOutcome.PersistenceFailed, accepted, Error: new AggregateException(error, storageError));
                }
                return new(outcome, accepted, Error: error);
            }
        }), requireAdmission: true);
}
