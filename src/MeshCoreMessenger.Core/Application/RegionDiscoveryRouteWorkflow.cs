using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;

namespace MeshCoreMessenger.Core.Application;

public interface IRegionDiscoveryRouteWorkflow
{
    Task<RegionRouteRequestResult> RequestAsync(RegionDiscoveryPlan plan, RegionDiscoveryPreparationConsent consent,
        TimeSpan? responseTimeout = null, CancellationToken cancellationToken = default);
    Task<RegionRouteRequestResult> RequestWithRetryAsync(RegionDiscoveryPlan plan, RegionDiscoveryPreparationConsent consent,
        TimeSpan? responseTimeout, int maximumAttempts, TimeSpan retryDelay, CancellationToken cancellationToken = default);
}

/// <summary>Owns one full-key contact throughout direct preparation, remote completion and bounded conditional restore.</summary>
public sealed class RegionDiscoveryRouteWorkflow(ISessionCommandGateway gateway, IDirectoryStore directories,
    ConversationOperationGuard operations, TimeProvider timeProvider) : IRegionDiscoveryRouteWorkflow
{
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public Task<RegionRouteRequestResult> RequestAsync(RegionDiscoveryPlan plan, RegionDiscoveryPreparationConsent consent,
        TimeSpan? responseTimeout = null, CancellationToken cancellationToken = default) =>
        RequestWithRetryAsync(plan, consent, responseTimeout, 1, TimeSpan.Zero, cancellationToken);

    public async Task<RegionRouteRequestResult> RequestWithRetryAsync(RegionDiscoveryPlan plan, RegionDiscoveryPreparationConsent consent,
        TimeSpan? responseTimeout, int maximumAttempts, TimeSpan retryDelay, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(consent);
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumAttempts is < 1 or > 3 || retryDelay < TimeSpan.Zero || retryDelay > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        if (CleanupTimeout <= TimeSpan.Zero || CleanupTimeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(CleanupTimeout));
        // Match the public library's bounded request timeout before making any route mutation.
        if (responseTimeout is { } wait && (wait < TimeSpan.FromMilliseconds(1) || wait > TimeSpan.FromMinutes(1)))
            throw new ArgumentOutOfRangeException(nameof(responseTimeout));
        if (plan.Preparation == RegionDiscoveryPreparation.NoUsableReplyPath || plan.ReplyPath is null ||
            plan.MayCreateContact && !consent.AllowContactCreation || plan.RequiresConditionalRestore && !consent.AllowTemporaryZeroHop)
            return new(RegionRoutePreparationOutcome.Rejected, RegionRouteRestorationOutcome.NotRequired, RegionContactCreationOutcome.NotApplicable, false);
        var key = plan.GetPublicKey();
        using var ownership = operations.BeginExclusive(plan.NodeId, ConversationKind.Contact, key);
        await using var lease = gateway.Acquire(plan.NodeId, new ContactCommandTarget(key), cancellationToken);
        if (!plan.MatchesOwner(lease.Owner.NodeId, lease.Owner.SessionId, lease.Owner.Generation))
            return new(RegionRoutePreparationOutcome.StalePlan, RegionRouteRestorationOutcome.NotRequired, RegionContactCreationOutcome.NotApplicable, false);
        return await lease.RunAsync(async (owned, token) =>
        {
            Contact? original;
            var initialVersion = owned.ContactObservationVersion(key);
            try
            {
                original = await ReadContactAsync(owned).ConfigureAwait(false);
                await owned.FlushClientEventsAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            catch (Exception error) { return new RegionRouteRequestResult(RegionRoutePreparationOutcome.Failed, RegionRouteRestorationOutcome.NotRequired, RegionContactCreationOutcome.NotApplicable, false, Error: error); }
            if (initialVersion != plan.ContactObservationVersion || initialVersion != owned.ContactObservationVersion(key) ||
                (plan.GetContactSnapshot() is null ? original is not null : original is null || !plan.MatchesContact(original)))
                return new(RegionRoutePreparationOutcome.StalePlan, RegionRouteRestorationOutcome.NotRequired, RegionContactCreationOutcome.NotApplicable, false);

            var preparation = plan.MayCreateContact ? RegionRoutePreparationOutcome.ContactCreationAllowed : RegionRoutePreparationOutcome.NotRequired;
            RegionDiscoveryPlan? preparedState = null;
            if (plan.RequiresConditionalRestore)
            {
                var zeroHop = ContactConfiguration.FromContact(original!) with { OutPathLength = 0, OutPath = new byte[ProtocolLimits.ContactPathSize] };
                try { await owned.AddOrUpdateContactAsync(zeroHop).ConfigureAwait(false); }
                catch (Exception error) when (error is MeshCoreCommandException or ArgumentException or NotSupportedException)
                { return new(RegionRoutePreparationOutcome.Rejected, RegionRouteRestorationOutcome.NotRequired, RegionContactCreationOutcome.NotApplicable, false, Error: error); }
                catch (Exception error) { return new(RegionRoutePreparationOutcome.Uncertain, owned.IsSessionOpen ? RegionRouteRestorationOutcome.UnsafeToRestore : RegionRouteRestorationOutcome.SessionClosed, RegionContactCreationOutcome.NotApplicable, false, Error: error); }
                try
                {
                    // Once SET has a definite OK, prove its state independently of caller cancellation.
                    // This proof also allows bounded restoration if cancellation precedes the Mesh request.
                    using var proofTimeout = new CancellationTokenSource(CleanupTimeout, timeProvider);
                    await using var proof = owned.CreateCleanupLease(proofTimeout.Token);
                    var observedUtc = timeProvider.GetUtcNow();
                    var prepared = await proof.GetContactAsync().ConfigureAwait(false);
                    await proof.FlushClientEventsAsync().ConfigureAwait(false);
                    if (prepared is null || !MatchesConfiguration(prepared, zeroHop))
                        return new(RegionRoutePreparationOutcome.Uncertain, RegionRouteRestorationOutcome.Conflict, RegionContactCreationOutcome.NotApplicable, false);
                    preparedState = new(plan.NodeId, plan.SessionId, plan.Generation, key, prepared, RegionDiscoveryPreparation.ReadyDirect,
                        plan.ReplyPath, plan.ReplyPathEvidence, observedUtc, contactObservationVersion: owned.ContactObservationVersion(key));
                    await PersistAsync(proof, prepared, observedUtc, proof.Token).ConfigureAwait(false);
                    preparation = RegionRoutePreparationOutcome.ZeroHopPrepared;
                }
                catch (Exception error)
                {
                    return new(RegionRoutePreparationOutcome.Uncertain, owned.IsSessionOpen ? RegionRouteRestorationOutcome.UnsafeToRestore : RegionRouteRestorationOutcome.SessionClosed,
                        RegionContactCreationOutcome.NotApplicable, false, Error: error);
                }
            }

            RegionNamesResponse? response = null; Exception? requestError = null; var requestInvoked = false;
            var retryState = preparedState ?? (original is not null ? plan : null);
            try
            {
                for (var attempt = 0; attempt < maximumAttempts; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    if (attempt > 0)
                    {
                        await Task.Delay(retryDelay, timeProvider, token).ConfigureAwait(false);
                        var observed = await ReadContactAsync(owned).ConfigureAwait(false);
                        await owned.FlushClientEventsAsync().ConfigureAwait(false);
                        if (retryState is not null && (observed is null || !retryState.MatchesContact(observed) ||
                            owned.ContactObservationVersion(key) != retryState.ContactObservationVersion))
                            throw new InvalidOperationException("Contact changed before the next region request.");
                        // Firmware may have created an initially absent zero-hop contact on the first send.
                        if (retryState is null && observed is not null)
                        {
                            if (observed.OutPathLength != 0) throw new InvalidOperationException("The new contact no longer has a zero-hop path.");
                            retryState = new(plan.NodeId, plan.SessionId, plan.Generation, key, observed,
                                RegionDiscoveryPreparation.ReadyDirect, plan.ReplyPath, plan.ReplyPathEvidence, timeProvider.GetUtcNow(),
                                contactObservationVersion: owned.ContactObservationVersion(key));
                        }
                    }
                    try
                    {
                        requestInvoked = true;
                        var received = await owned.RequestRegionsAsync(plan.ReplyPath, responseTimeout).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested(); response = received; requestError = null; break;
                    }
                    catch (MeshCoreTimeoutException timeout) when (timeout.Operation == "RemoteBinaryResponse" && attempt + 1 < maximumAttempts)
                    {
                        requestError = timeout;
                        System.Diagnostics.Trace.TraceInformation("Region response timed out after attempt {0}; a bounded retry will follow", attempt + 1);
                    }
                }
            }
            catch (Exception error) { requestError = error; }
            var restoration = RegionRouteRestorationOutcome.NotRequired;
            var creation = plan.MayCreateContact ? RegionContactCreationOutcome.Unknown : RegionContactCreationOutcome.NotApplicable;
            Exception? cleanupError = null;
            if (preparedState is not null || plan.MayCreateContact && requestInvoked)
            {
                if (!owned.IsSessionOpen) restoration = RegionRouteRestorationOutcome.SessionClosed;
                else
                {
                    using var cleanup = new CancellationTokenSource(CleanupTimeout, timeProvider);
                    try
                    {
                        await using var cleaner = owned.CreateCleanupLease(cleanup.Token);
                        await cleaner.RunAsync(async (active, cleanupToken) =>
                        {
                            var observedUtc = timeProvider.GetUtcNow();
                            var current = await ReadContactAsync(active).ConfigureAwait(false);
                            await active.FlushClientEventsAsync().ConfigureAwait(false);
                            if (plan.MayCreateContact)
                            {
                                creation = current is null ? RegionContactCreationOutcome.NotObserved : RegionContactCreationOutcome.Observed;
                                if (current is not null) await PersistAsync(active, current, observedUtc, cleanupToken).ConfigureAwait(false);
                                return true; // Never delete a contact implicitly created by firmware.
                            }
                            if (current is null || !preparedState!.MatchesContact(current) || active.ContactObservationVersion(key) != preparedState.ContactObservationVersion)
                            {
                                restoration = RegionRouteRestorationOutcome.Conflict;
                                if (current is not null) await PersistAsync(active, current, observedUtc, cleanupToken).ConfigureAwait(false);
                                return true;
                            }
                            await active.AddOrUpdateContactAsync(ContactConfiguration.FromContact(original!)).ConfigureAwait(false);
                            observedUtc = timeProvider.GetUtcNow();
                            var restored = await ReadContactAsync(active).ConfigureAwait(false);
                            await active.FlushClientEventsAsync().ConfigureAwait(false);
                            if (restored is null || !MatchesConfiguration(restored, ContactConfiguration.FromContact(original!)))
                            {
                                restoration = RegionRouteRestorationOutcome.Conflict;
                                if (restored is not null) await PersistAsync(active, restored, observedUtc, cleanupToken).ConfigureAwait(false);
                                return true;
                            }
                            await PersistAsync(active, restored, observedUtc, cleanupToken).ConfigureAwait(false);
                            restoration = RegionRouteRestorationOutcome.Restored;
                            return true;
                        }).ConfigureAwait(false);
                    }
                    catch (Exception error) { cleanupError = error; restoration = owned.IsSessionOpen ? RegionRouteRestorationOutcome.Failed : RegionRouteRestorationOutcome.SessionClosed; }
                }
            }
            if (plan.MayCreateContact && !requestInvoked) creation = RegionContactCreationOutcome.NotObserved;
            return new(preparation, restoration, creation, requestInvoked, response, requestError, cleanupError);
        }).ConfigureAwait(false);
    }

    private static async Task<Contact?> ReadContactAsync(SessionCommandLease lease)
    {
        try
        {
            var contact = await lease.GetContactAsync().ConfigureAwait(false);
            if (lease.Target is not ContactCommandTarget target || !contact.PublicKey.Span.SequenceEqual(target.PublicKey.Span))
                throw new MeshCoreProtocolException("Contact readback returned a different full key.");
            return contact with { PublicKey = contact.PublicKey.ToArray(), OutPath = contact.OutPath.ToArray() };
        }
        catch (MeshCoreCommandException error) when (error.ErrorCode == MeshCoreErrorCode.NotFound) { return null; }
    }
    private Task PersistAsync(SessionCommandLease owner, Contact contact, DateTimeOffset observedUtc, CancellationToken token) =>
        owner.PersistContactReadbackAsync(directories, contact, observedUtc, token);
    private static bool MatchesConfiguration(Contact actual, ContactConfiguration expected) =>
        actual.PublicKey.Span.SequenceEqual(expected.PublicKey.Span) && actual.AdvertisementType == expected.AdvertisementType &&
        actual.Flags == expected.Flags && actual.Name == expected.Name && actual.LastAdvertTimestamp == expected.LastAdvertTimestamp &&
        actual.AdvertisementLatitude.Equals(expected.AdvertisementLatitude) && actual.AdvertisementLongitude.Equals(expected.AdvertisementLongitude) &&
        actual.OutPathLength == expected.OutPathLength && actual.OutPath.Span.SequenceEqual(expected.OutPath.Span);
}
