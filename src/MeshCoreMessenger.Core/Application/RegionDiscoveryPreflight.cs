using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;

namespace MeshCoreMessenger.Core.Application;

public interface IRegionDiscoveryPreflight
{
    Task<RegionDiscoveryPreflightResult> PrepareAsync(SessionCommandOwner owner, IReadOnlyList<DiscoveredNeighbor> neighbors,
        IReadOnlyList<ConfirmedRegionReplyPath>? confirmedReplyPaths = null, int maximumTargets = 128, CancellationToken cancellationToken = default);
}

/// <summary>Refreshes full-key contacts via leases; does not mutate contacts/routes or send a Mesh request.</summary>
public sealed class RegionDiscoveryPreflight(ISessionCommandGateway gateway, TimeProvider timeProvider) : IRegionDiscoveryPreflight
{
    public async Task<RegionDiscoveryPreflightResult> PrepareAsync(SessionCommandOwner owner, IReadOnlyList<DiscoveredNeighbor> neighbors,
        IReadOnlyList<ConfirmedRegionReplyPath>? confirmedReplyPaths = null, int maximumTargets = 128, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(neighbors);
        if (maximumTargets is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maximumTargets));
        var observed = neighbors.ToArray();
        if (observed.Any(n => n is null)) throw new ArgumentException("Null neighbor.", nameof(neighbors));
        var confirmed = (confirmedReplyPaths ?? []).ToDictionary(p => Convert.ToHexString(p.GetPublicKey()), p => p.ReplyPath, StringComparer.Ordinal);
        await using var node = gateway.Acquire(owner.NodeId, new NodeCommandTarget(), cancellationToken);
        EnsureOwner(node, owner);
        return await node.RunAsync(async (_, token) =>
        {
            var all = await node.GetContactsAsync().ConfigureAwait(false);
            var contacts = all.ToDictionary(c => c.PublicKeyHex, StringComparer.Ordinal);
            var repeaters = observed.Where(n => n.NodeType == AdvertisementType.Repeater).ToArray();
            var fullNeighbors = repeaters.Where(n => n.HasFullPublicKey).GroupBy(n => Convert.ToHexString(n.GetKeyBytes()), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Any(n => (n.PathDescriptor & 63) == 0 && (n.PathDescriptor >> 6) < 3), StringComparer.Ordinal);
            var keys = contacts.Values.Where(c => c.AdvertisementType == AdvertisementType.Repeater).Select(c => c.PublicKeyHex)
                .Concat(fullNeighbors.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var plans = new List<RegionDiscoveryPlan>();
            foreach (var key in keys.Take(maximumTargets))
            {
                token.ThrowIfCancellationRequested();
                var publicKey = Convert.FromHexString(key);
                var nearby = fullNeighbors.GetValueOrDefault(key);
                Contact? current = null;
                var observationVersion = node.ContactObservationVersion(publicKey);
                if (contacts.ContainsKey(key))
                {
                    await using var target = gateway.Acquire(owner.NodeId, new ContactCommandTarget(publicKey), token);
                    EnsureOwner(target, owner);
                    try { current = await target.GetContactAsync().ConfigureAwait(false); }
                    catch (MeshCoreCommandException error) when (error.ErrorCode == MeshCoreErrorCode.NotFound) { }
                    token.ThrowIfCancellationRequested();
                    await target.FlushClientEventsAsync().ConfigureAwait(false);
                    if (current is not null && !current.PublicKey.Span.SequenceEqual(publicKey))
                        throw new MeshCoreProtocolException("Preflight readback returned a different full contact key.");
                }
                var readConflict = observationVersion != node.ContactObservationVersion(publicKey);
                var provided = confirmed.GetValueOrDefault(key);
                var preparation = RegionDiscoveryPreparation.NoUsableReplyPath;
                var evidence = provided is null ? RegionReplyPathEvidence.None : RegionReplyPathEvidence.Confirmed;
                ReplyPath? reply = provided;
                string? reason = null;
                if (current is null)
                {
                    if (nearby)
                    {
                        preparation = RegionDiscoveryPreparation.RequiresContactCreation;
                        reply ??= ReplyPath.ZeroHop;
                        if (provided is null) evidence = RegionReplyPathEvidence.ObservedZeroHopNeighbor;
                        reason = "The anonymous request may create a Companion contact; refresh is required afterwards.";
                    }
                    else reason = "No current direct outbound path is known.";
                }
                else if (current.AdvertisementType != AdvertisementType.Repeater) reason = "The current contact is not a repeater.";
                else if (current.OutPathLength == byte.MaxValue)
                {
                    if (nearby)
                    {
                        preparation = RegionDiscoveryPreparation.RequiresTemporaryZeroHop;
                        reply ??= ReplyPath.ZeroHop;
                        if (provided is null) evidence = RegionReplyPathEvidence.ObservedZeroHopNeighbor;
                        reason = "Snapshot, zero-hop mutation/readback and conditional restore/readback are required.";
                    }
                    else reason = "A flood contact cannot receive a direct-only region request.";
                }
                else
                {
                    try
                    {
                        var count = (current.OutPathLength & 63) * ((current.OutPathLength >> 6) + 1);
                        var outbound = new ReplyPath(current.OutPathLength, current.OutPath.Span[..count]);
                        preparation = RegionDiscoveryPreparation.ReadyDirect;
                        reply ??= outbound.ReverseHops();
                        if (provided is null) evidence = outbound.HopCount == 0 ? RegionReplyPathEvidence.CurrentZeroHopPath : RegionReplyPathEvidence.AssumedReverse;
                    }
                    catch (ArgumentException) { reason = "The current packed outbound path is invalid."; reply = null; evidence = RegionReplyPathEvidence.None; }
                }
                if (readConflict) { preparation = RegionDiscoveryPreparation.NoUsableReplyPath; reason = "The contact changed while preflight was reading it; prepare a new plan."; }
                plans.Add(new(owner.NodeId, owner.SessionId, owner.Generation, publicKey, current, preparation, reply, evidence, timeProvider.GetUtcNow(), reason, observationVersion));
            }
            token.ThrowIfCancellationRequested();
            return new RegionDiscoveryPreflightResult(plans.AsReadOnly(), repeaters.Count(n => !n.HasFullPublicKey), Math.Max(0, keys.Length - maximumTargets));
        }).ConfigureAwait(false);
    }

    private static void EnsureOwner(SessionCommandLease lease, SessionCommandOwner owner)
    {
        if (lease.Owner.SessionId != owner.SessionId || lease.Owner.Generation != owner.Generation)
            throw new InvalidOperationException("Discovery preflight belongs to a stale session.");
    }
}
