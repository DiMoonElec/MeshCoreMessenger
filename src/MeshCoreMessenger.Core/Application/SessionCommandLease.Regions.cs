using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Application;

public sealed partial class SessionCommandLease
{
    internal long ContactObservationVersion(ReadOnlyMemory<byte> key) => _scope.ContactObservationVersion(key);
    internal bool IsSessionOpen => _scope.IsOpen;
    internal SessionCommandLease CreateCleanupLease(CancellationToken cleanupToken)
    {
        lock (_scope.Gate) { EnsureUsable(); _scope.EnsureOpen(); return _scope.Acquire(Target, cleanupToken); }
    }
    internal Task FlushClientEventsAsync() => OwnAsync(() => _scope.Invoke(this, async (client, token) =>
    { await client.FlushEventsAsync(token).ConfigureAwait(false); return true; }), requireAdmission: true);
    internal Task PersistContactReadbackAsync(IDirectoryStore store, Contact contact, DateTimeOffset observedUtc, CancellationToken token) =>
        OwnAsync(async () =>
        {
            token.ThrowIfCancellationRequested();
            await store.ApplyContactReadbackAsync(Owner.NodeId, Owner.SessionId, DirectoryService.ToSnapshot(contact), observedUtc, token).ConfigureAwait(false);
            return true;
        }, requireAdmission: true);
    internal Task<RegionNamesResponse> RequestRegionsAsync(ReplyPath path, TimeSpan? timeout = null)
    {
        if (Target is not ContactCommandTarget contact) throw new InvalidOperationException("A full repeater target is required.");
        return OwnAsync(() => _scope.Invoke(this, (client, token) => client.RequestRegionsAsync(contact.PublicKey, path, timeout, token)), requireAdmission: true);
    }
    internal Task<NeighborDiscoveryResult> DiscoverRepeaterNeighborsAsync(NeighborDiscoveryOptions options) =>
        OwnAsync(() => _scope.Invoke(this, (client, token) => client.DiscoverRepeaterNeighborsAsync(options, token)), requireAdmission: true);
    internal Task PersistDiscoveryOutcomeAsync(IRegionStore store, RegionDiscoveryTargetOutcome outcome, IReadOnlyList<RegionObservation> observations,
        int maximumObservations, int maximumOutcomes, DateTimeOffset retainSince) => OwnAsync(async () =>
        {
            if (!_scope.IsOpen) throw new InvalidOperationException("Discovery session closed before persistence.");
            await store.SaveDiscoveryOutcomeAsync(outcome, observations, maximumObservations, maximumOutcomes, retainSince, CancellationToken.None).ConfigureAwait(false);
            return true;
        }, requireAdmission: false);
    internal static FloodScopeSelection WireScope(ResolvedRegionScope scope) => scope.State switch
    {
        StoredRegionScopeState.KnownScoped => FloodScopeSelection.ExplicitRegion(scope.Key!),
        StoredRegionScopeState.KnownUnscoped => FloodScopeSelection.ForceUnscoped,
        _ when scope.RequestedMode == FloodScopeMode.InheritDefault => FloodScopeSelection.InheritDefault,
        _ => throw new InvalidOperationException("An explicit scope requires a complete immutable capture."),
    };

    private FloodScopeSelection ScopeForSend(ResolvedRegionScope captured)
    {
        var capabilities = _scope.Capabilities;
        if (captured.RequestedMode == FloodScopeMode.InheritDefault &&
            (captured.State == StoredRegionScopeState.KnownUnscoped && capabilities?.ForceUnscoped == RegionSupport.Unsupported ||
             captured.State == StoredRegionScopeState.KnownScoped && capabilities?.TemporaryScope == RegionSupport.Unsupported))
        {
            // Older firmware cannot pin every scope. Inherit remains safe only while the
            // observed default is still identical, under the gate shared with default writes.
            var current = _scope.RegionSnapshot;
            if (current?.IsCurrent(Owner.SessionId, Owner.Generation) == true &&
                (captured.State == StoredRegionScopeState.KnownUnscoped && (current.Value.State == NodeFloodScopeState.KnownEmpty || current.Value.Key?.IsEmpty == true) ||
                 captured.State == StoredRegionScopeState.KnownScoped && Equals(captured.Key, current.Value.Key)))
                return FloodScopeSelection.InheritDefault;
            throw new NotSupportedException("This firmware cannot reproduce the captured scope after its default changed.");
        }
        return WireScope(captured);
    }

    private ResolvedRegionScope CompleteScopeCapture(ResolvedRegionScope scope, Action<long>? revisionCaptured = null)
    {
        var limit = _scope.GetTextByteLimit(Target is ChannelCommandTarget, WireScope(scope));
        revisionCaptured?.Invoke(_scope.RegionSnapshot?.Revision ?? 0);
        return new(scope.NodeId, scope.SessionId, scope.Generation, scope.RequestedMode, scope.PolicyRevision, scope.State, scope.Key, scope.Name, limit);
    }

    internal Task<ResolvedRegionScope> RefreshCapturedBudgetAsync(ResolvedRegionScope scope, Action<long>? revisionCaptured = null) =>
        _scope.WithRegionStateAsync(this, () => Task.FromResult(CompleteScopeCapture(scope, revisionCaptured)));

    internal Task<ResolvedRegionScope> ResolveDefaultScopeAsync(int budget, Action<long>? revisionCaptured = null) => _scope.WithRegionStateAsync(this, () =>
    {
        var snapshot = _scope.RegionSnapshot;
        // Firmware without temporary scope cannot promise a reproducible region over retries.
        var value = _scope.Capabilities?.TemporaryScope != RegionSupport.Unsupported &&
            snapshot?.IsCurrent(Owner.SessionId, Owner.Generation) == true ? snapshot.Value : NodeFloodScope.Unknown;
        var state = value.State == NodeFloodScopeState.KnownEmpty || value.Key?.IsEmpty == true ? StoredRegionScopeState.KnownUnscoped :
            value.State == NodeFloodScopeState.Configured ? StoredRegionScopeState.KnownScoped : StoredRegionScopeState.Unknown;
        return Task.FromResult(CompleteScopeCapture(new(Owner.NodeId, Owner.SessionId, Owner.Generation, FloodScopeMode.InheritDefault,
            snapshot?.Revision ?? 0, state, state == StoredRegionScopeState.KnownScoped ? value.Key : null, value.Name, budget), revisionCaptured));
    });

    internal async Task<ResolvedRegionScope> ResolveChannelScopeAsync(IRegionReader regions, IDirectoryStore directories, int budget, Action<long>? revisionCaptured = null)
    {
        if (Target is not ChannelCommandTarget channel) throw new InvalidOperationException("Channel scope requires a binding.");
        var binding = await directories.GetActiveChannelBindingAsync(Owner.NodeId, channel.Recipient.Slot!.Value, Token).ConfigureAwait(false);
        if (binding is null || binding.Id != channel.Recipient.ChannelBindingId || binding.Generation != channel.Recipient.BindingGeneration)
            throw new InvalidOperationException("Channel binding changed before scope capture.");
        var preference = await regions.GetChannelPreferenceAsync(Owner.NodeId, binding.ChannelId, Token).ConfigureAwait(false);
        var definition = preference.RegionId is { } id
            ? (await regions.GetRegionAsync(Owner.NodeId, id, Token).ConfigureAwait(false))?.Definition
                ?? throw new InvalidOperationException("Assigned region no longer exists.") : null;
        return await _scope.WithRegionStateAsync(this, () =>
        {
            var mode = preference.Mode;
            var snapshot = _scope.RegionSnapshot;
            var value = snapshot?.IsCurrent(Owner.SessionId, Owner.Generation) == true ? snapshot.Value : NodeFloodScope.Unknown;
            var state = mode == FloodScopeMode.ExplicitRegion ? StoredRegionScopeState.KnownScoped :
                mode == FloodScopeMode.ForceUnscoped || value.State == NodeFloodScopeState.KnownEmpty || value.Key?.IsEmpty == true
                    ? StoredRegionScopeState.KnownUnscoped : value.State == NodeFloodScopeState.Configured
                        ? StoredRegionScopeState.KnownScoped : StoredRegionScopeState.Unknown;
            return Task.FromResult(CompleteScopeCapture(new(Owner.NodeId, Owner.SessionId, Owner.Generation, mode, preference.Revision,
                state, state == StoredRegionScopeState.KnownScoped ? definition?.Key ?? value.Key : null,
                mode == FloodScopeMode.ExplicitRegion ? definition!.Name : mode == FloodScopeMode.InheritDefault ? value.Name : null, budget), revisionCaptured));
        }).ConfigureAwait(false);
    }

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
