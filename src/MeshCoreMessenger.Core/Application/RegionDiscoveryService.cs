using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using System.Text;

namespace MeshCoreMessenger.Core.Application;

public interface IRegionDiscoveryService
{
    Task<RegionDiscoveryRunResult> DiscoverAsync(SessionCommandOwner owner, RegionDiscoveryOptions? options = null,
        IProgress<RegionDiscoveryProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>One explicit bounded scan/query operation; catalogue saves and scope selection remain explicit.</summary>
public sealed class RegionDiscoveryService(ISessionCommandGateway gateway, IRegionDiscoveryPreflight preflight,
    IRegionDiscoveryRouteWorkflow routes, IRegionStore store, TimeProvider timeProvider) : IRegionDiscoveryService
{
    private readonly SemaphoreSlim _operation = new(1, 1);
    public async Task<RegionDiscoveryRunResult> DiscoverAsync(SessionCommandOwner owner, RegionDiscoveryOptions? options = null,
        IProgress<RegionDiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!await _operation.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Region discovery is already running.");
        try { return await DiscoverCoreAsync(owner, options, progress, cancellationToken).ConfigureAwait(false); }
        finally { _operation.Release(); }
    }

    private async Task<RegionDiscoveryRunResult> DiscoverCoreAsync(SessionCommandOwner owner, RegionDiscoveryOptions? options,
        IProgress<RegionDiscoveryProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner); options ??= new(); Validate(options);
        var selected = options.SelectedTargetPublicKeys?.Select(FullKey).ToHashSet(StringComparer.Ordinal);
        options = options with { Neighbors = options.Neighbors with { }, ConfirmedReplyPaths = options.ConfirmedReplyPaths.ToArray(),
            SelectedTargetPublicKeys = selected?.ToArray() };
        using var deadline = new CancellationTokenSource(options.TotalTimeout ?? Timeout.InfiniteTimeSpan, timeProvider);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        await using var lease = gateway.Acquire(owner.NodeId, new NodeCommandTarget(), stop.Token);
        if (lease.Owner.SessionId != owner.SessionId || lease.Owner.Generation != owner.Generation)
            throw new InvalidOperationException("Discovery owner is no longer active.");
        var run = Guid.NewGuid(); var outcomes = new List<RegionDiscoveryTargetOutcome>(); var plans = new List<RegionDiscoveryPlan>();
        var omitted = 0; var unresolvedPrefixes = 0; var capacity = false; Exception? runError = null;
        return await lease.RunAsync(async (owned, token) =>
        {
            try
            {
                Report(RegionDiscoveryPhase.Scanning, 0, 0);
                NeighborDiscoveryResult neighbors;
                try { neighbors = await owned.DiscoverRepeaterNeighborsAsync(options.Neighbors).ConfigureAwait(false); }
                catch (Exception error) when (error is NotSupportedException or MeshCoreCommandException { ErrorCode: MeshCoreErrorCode.UnsupportedCommand })
                { runError = error; neighbors = new(0, [], false); }
                var discoveredRepeaters = neighbors.Neighbors.Where(n => n.NodeType == AdvertisementType.Repeater && n.HasFullPublicKey &&
                        (!options.OnlyDiscoveredNeighbors || (n.PathDescriptor & 63) == 0 && (n.PathDescriptor >> 6) < 3))
                    .OrderByDescending(n => n.ReceivedRssi).ThenByDescending(n => n.ReceivedSnr)
                    .ThenBy(n => Convert.ToHexString(n.GetKeyBytes()), StringComparer.Ordinal)
                    .DistinctBy(n => Convert.ToHexString(n.GetKeyBytes())).ToArray();
                Report(RegionDiscoveryPhase.Scanning, 0, discoveredRepeaters.Length, neighbors: discoveredRepeaters);
                capacity = neighbors.CapacityReached;
                Report(RegionDiscoveryPhase.Preparing, 0, 0);
                // Mesh target count is separate from read-only preflight capacity, so explicit selections
                // are not incorrectly lost behind earlier known contacts in a sorted bounded list.
                var prepared = options.OnlyDiscoveredNeighbors && discoveredRepeaters.Length == 0
                    ? new RegionDiscoveryPreflightResult(Array.Empty<RegionDiscoveryPlan>(), 0, 0)
                    : await preflight.PrepareAsync(owner,
                        options.OnlyDiscoveredNeighbors ? discoveredRepeaters.Take(options.MaximumTargets).ToArray() : neighbors.Neighbors,
                        options.OnlyDiscoveredNeighbors ? [] : options.ConfirmedReplyPaths, 1024, token, options.OnlyDiscoveredNeighbors).ConfigureAwait(false);
                unresolvedPrefixes = prepared.UnresolvedPrefixCount;
                IEnumerable<RegionDiscoveryPlan> eligible = prepared.Plans;
                if (selected is not null && !options.OnlyDiscoveredNeighbors)
                {
                    eligible = eligible.Where(p => selected.Contains(Convert.ToHexString(p.GetPublicKey())));
                    var known = prepared.Plans.Select(p => Convert.ToHexString(p.GetPublicKey())).ToHashSet(StringComparer.Ordinal);
                    eligible = eligible.Concat(selected.Where(key => !known.Contains(key)).Select(key => new RegionDiscoveryPlan(owner.NodeId, owner.SessionId, owner.Generation,
                        Convert.FromHexString(key), null, RegionDiscoveryPreparation.NoUsableReplyPath, null, RegionReplyPathEvidence.None,
                        timeProvider.GetUtcNow(), "The selected full key has no current route or neighbor observation.")));
                }
                var all = eligible.ToArray(); omitted = prepared.OmittedTargetCount + Math.Max(0, all.Length - options.MaximumTargets);
                if (options.OnlyDiscoveredNeighbors) omitted += Math.Max(0, discoveredRepeaters.Length - options.MaximumTargets);
                plans.AddRange(all.Take(options.MaximumTargets));
                var neighborKeys = neighbors.Neighbors.Where(n => n.HasFullPublicKey).Select(n => Convert.ToHexString(n.GetKeyBytes())).ToHashSet(StringComparer.Ordinal);
                foreach (var plan in plans)
                {
                    var key = Convert.ToHexString(plan.GetPublicKey());
                    if (token.IsCancellationRequested || !owned.IsSessionOpen) break;
                    Report(RegionDiscoveryPhase.Requesting, outcomes.Count, plans.Count, key);
                    RegionRouteRequestResult? route = null; Exception? error = null;
                    try { route = await routes.RequestWithRetryAsync(plan, options.Consent, options.RequestTimeout,
                        options.MaximumAttempts, options.RetryDelay, token).ConfigureAwait(false); }
                    catch (Exception failure) { error = failure; }
                    error ??= route?.Error;
                    IReadOnlyList<string> names = Array.AsReadOnly(route?.Response?.Names.ToArray() ?? []);
                    var status = Status(plan, route, error);
                    if (error is OperationCanceledException && deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested && owned.IsSessionOpen)
                        status = RegionDiscoveryTargetStatus.Timeout;
                    try { ValidateNames(names); }
                    catch (ArgumentException malformed) { error = malformed; status = RegionDiscoveryTargetStatus.Malformed; names = []; }
                    var outcome = new RegionDiscoveryTargetOutcome(run, owner.NodeId, owner.SessionId, owner.Generation, key, timeProvider.GetUtcNow(), status, names,
                        route?.Preparation ?? RegionRoutePreparationOutcome.NotRequired, route?.Restoration ?? RegionRouteRestorationOutcome.NotRequired,
                        route?.ContactCreation ?? RegionContactCreationOutcome.NotApplicable, ErrorCode(error ?? route?.CleanupError));
                    var evidence = IsSuccess(status) ? names.Distinct(StringComparer.Ordinal).Select(name => new RegionObservation(owner.NodeId, plan.GetPublicKey(), name,
                        neighborKeys.Contains(key) ? RegionObservationSource.Neighbor : RegionObservationSource.KnownRepeater, outcome.CompletedUtc, outcome.CompletedUtc)).ToArray() : [];
                    if (owned.IsSessionOpen)
                    {
                        try
                        {
                            await owned.PersistDiscoveryOutcomeAsync(store, outcome, evidence, options.MaximumStoredObservations, options.MaximumStoredOutcomes,
                                timeProvider.GetUtcNow() - options.Retention).ConfigureAwait(false);
                            outcome = outcome with { Persisted = true };
                        }
                        catch (Exception storageError)
                        {
                            runError = storageError;
                            outcomes.Add(outcome with { Status = RegionDiscoveryTargetStatus.StorageFailed, ErrorCode = ErrorCode(storageError) });
                            break;
                        }
                    }
                    outcomes.Add(outcome);
                    Report(RegionDiscoveryPhase.Requesting, outcomes.Count, plans.Count, key, names: AggregateNames());
                    if (!owned.IsSessionOpen || status == RegionDiscoveryTargetStatus.TransportFailed) break;
                }
            }
            catch (Exception error) { runError = error; }
            Report(RegionDiscoveryPhase.Completed, outcomes.Count, plans.Count);
            var namesBySource = AggregateNames();
            return new RegionDiscoveryRunResult(run, new(owner.NodeId, owner.SessionId, owner.Generation), plans.AsReadOnly(), outcomes.AsReadOnly(),
                Array.AsReadOnly(namesBySource), cancellationToken.IsCancellationRequested, deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested,
                !owned.IsSessionOpen, capacity, omitted, runError) { UnresolvedPrefixCount = unresolvedPrefixes };
        }).ConfigureAwait(false);

        DiscoveredRegionName[] AggregateNames() => outcomes.Where(o => o.Persisted && IsSuccess(o.Status))
            .SelectMany(o => o.Names.Select(name => (Name: name, Key: o.RepeaterPublicKeyHex)))
            .GroupBy(o => o.Name, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new DiscoveredRegionName(g.Key, Classify(g.Key), Array.AsReadOnly(g.Select(x => x.Key).Distinct(StringComparer.Ordinal).ToArray()))).ToArray();
        void Report(RegionDiscoveryPhase phase, int completed, int total, string? key = null, IReadOnlyList<DiscoveredNeighbor>? neighbors = null,
            IReadOnlyList<DiscoveredRegionName>? names = null)
        { try { progress?.Report(new(run, phase, completed, total, key, neighbors, names, new(owner.NodeId, owner.SessionId, owner.Generation))); } catch { /* Observers do not control cleanup. */ } }
    }

    private static bool IsSuccess(RegionDiscoveryTargetStatus status) => status is RegionDiscoveryTargetStatus.SuccessEmpty or RegionDiscoveryTargetStatus.SuccessPossiblyPartial or RegionDiscoveryTargetStatus.SuccessWithRouteWarning;
    private static RegionDiscoveryTargetStatus Status(RegionDiscoveryPlan plan, RegionRouteRequestResult? route, Exception? error)
    {
        if (route?.Response is { } response)
            return route.Restoration is not (RegionRouteRestorationOutcome.NotRequired or RegionRouteRestorationOutcome.Restored)
                ? RegionDiscoveryTargetStatus.SuccessWithRouteWarning : response.Names.Count == 0 ? RegionDiscoveryTargetStatus.SuccessEmpty : RegionDiscoveryTargetStatus.SuccessPossiblyPartial;
        if (plan.Preparation == RegionDiscoveryPreparation.NoUsableReplyPath) return RegionDiscoveryTargetStatus.NoDirectPath;
        if (route?.Preparation == RegionRoutePreparationOutcome.Rejected && error is null) return RegionDiscoveryTargetStatus.PreparationRequired;
        if (route?.Preparation == RegionRoutePreparationOutcome.StalePlan) return RegionDiscoveryTargetStatus.StalePlan;
        return error switch
        {
            MeshCoreCommandException { ErrorCode: MeshCoreErrorCode.TableFull } => RegionDiscoveryTargetStatus.ContactTableFull,
            NotSupportedException or MeshCoreCommandException { ErrorCode: MeshCoreErrorCode.UnsupportedCommand } => RegionDiscoveryTargetStatus.Unsupported,
            MeshCoreTimeoutException or TimeoutException => RegionDiscoveryTargetStatus.Timeout,
            OperationCanceledException => RegionDiscoveryTargetStatus.Cancelled,
            MeshCoreProtocolException or ArgumentException => RegionDiscoveryTargetStatus.Malformed,
            MeshCoreTransportException or IOException => RegionDiscoveryTargetStatus.TransportFailed,
            _ => RegionDiscoveryTargetStatus.PreparationFailed,
        };
    }
    private static string? ErrorCode(Exception? error) => error is MeshCoreCommandException command ? $"{command.Command}:{command.ErrorCode}" : error?.GetType().Name;
    private static DiscoveredRegionNameKind Classify(string name)
    {
        if (name == "*") return DiscoveredRegionNameKind.Unscoped;
        if (name.StartsWith('$')) return DiscoveredRegionNameKind.PrivateKeyRequired;
        try { _ = RegionKeys.NormalizePublicName(name); return DiscoveredRegionNameKind.Public; }
        catch (ArgumentException) { return DiscoveredRegionNameKind.Unrecognized; }
    }
    private static string FullKey(string key)
    { var bytes = Convert.FromHexString(key); return bytes.Length == ProtocolLimits.PublicKeySize ? Convert.ToHexString(bytes) : throw new ArgumentException("A full repeater key is required."); }
    private static void ValidateNames(IReadOnlyList<string> names)
    {
        var strict = new UTF8Encoding(false, true);
        if (names.Count > 128 || names.Any(n => string.IsNullOrEmpty(n) || n.IndexOfAny(['\0', ',']) >= 0 || strict.GetByteCount(n) > ProtocolLimits.MaxRegionNameUtf8Bytes) ||
            strict.GetByteCount(string.Join(',', names)) > ProtocolLimits.CurrentFirmwareMaxFrameSize)
            throw new ArgumentException("Region reply is not a bounded valid name list.");
    }
    private static void Validate(RegionDiscoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Neighbors); ArgumentNullException.ThrowIfNull(options.Consent); ArgumentNullException.ThrowIfNull(options.ConfirmedReplyPaths);
        if (options.MaximumAttempts is < 1 or > 3 || options.RetryDelay < TimeSpan.Zero || options.RetryDelay > TimeSpan.FromSeconds(30) ||
            options.MaximumTargets is < 1 or > 128 || options.MaximumStoredObservations is < 1 or > 4096 || options.MaximumStoredOutcomes is < 1 or > 4096 ||
            options.RequestTimeout < TimeSpan.FromMilliseconds(1) || options.RequestTimeout > TimeSpan.FromMinutes(1) ||
            options.TotalTimeout is { } total && (total <= TimeSpan.Zero || total > TimeSpan.FromMinutes(5)) ||
            options.Retention <= TimeSpan.Zero || options.Retention > TimeSpan.FromDays(365) ||
            options.SelectedTargetPublicKeys?.Count > 1024 || options.ConfirmedReplyPaths.Count > 1024 || options.ConfirmedReplyPaths.Any(p => p is null))
            throw new ArgumentException("Discovery options exceed bounded operation limits.", nameof(options));
    }
}
