using System.Security.Cryptography;
using System.Text;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;

namespace MeshCoreMessenger.Core.Domain;

public enum RegionProvenance { Manual = 0, Discovery = 1 }
public enum RegionSupport { Unknown = 0, Unsupported = 1, Supported = 2 }
public enum RegionSnapshotFreshness { Unknown = 0, CurrentSession = 1, Offline = 2 }
public enum RegionObservationSource { Neighbor = 0, KnownRepeater = 1 }
public enum StoredRegionScopeState { LegacyUnknown = 0, Unknown = 1, KnownScoped = 2, KnownUnscoped = 3 }

/// <summary>Local catalogue entry owned by a full-key NodeRecord, never a connection profile or channel slot.</summary>
public sealed class RegionCatalogEntry
{
    public RegionCatalogEntry(Guid id, Guid nodeId, string label, RegionDefinition definition, RegionProvenance provenance,
        DateTimeOffset createdUtc, DateTimeOffset updatedUtc, bool archived = false)
    {
        RegionModelValidation.Identity(id); RegionModelValidation.Identity(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (label.Contains('\0')) throw new ArgumentException("Region label contains NUL.", nameof(label));
        ArgumentNullException.ThrowIfNull(definition);
        if (!Enum.IsDefined(provenance)) throw new ArgumentOutOfRangeException(nameof(provenance));
        if (updatedUtc < createdUtc) throw new ArgumentOutOfRangeException(nameof(updatedUtc));
        Id = id; NodeId = nodeId; Label = label; Definition = definition; Provenance = provenance;
        CreatedUtc = createdUtc.ToUniversalTime(); UpdatedUtc = updatedUtc.ToUniversalTime(); Archived = archived;
        KeyFingerprint = Convert.ToHexString(SHA256.HashData(definition.Key.ToArray()));
    }
    public Guid Id { get; }
    public Guid NodeId { get; }
    public string Label { get; }
    public RegionDefinition Definition { get; }
    public string ProtocolName => Definition.Name;
    public string KeyFingerprint { get; }
    public RegionProvenance Provenance { get; }
    public DateTimeOffset CreatedUtc { get; }
    public DateTimeOffset UpdatedUtc { get; }
    public bool Archived { get; }
    public override string ToString() => $"Region {Id}: {Label}";
}

/// <summary>One policy per stable channel ID. Scope never becomes part of channel identity or binding generation.</summary>
public sealed class ChannelRegionPreference
{
    public ChannelRegionPreference(Guid nodeId, Guid channelId, FloodScopeMode mode, Guid? regionId, long revision)
    {
        RegionModelValidation.Identity(nodeId); RegionModelValidation.Identity(channelId);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if ((mode == FloodScopeMode.ExplicitRegion) != regionId.HasValue || regionId == Guid.Empty)
            throw new ArgumentException("Only ExplicitRegion requires a catalogue reference.", nameof(regionId));
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        NodeId = nodeId; ChannelId = channelId; Mode = mode; RegionId = regionId; Revision = revision;
    }
    public Guid NodeId { get; }
    public Guid ChannelId { get; }
    public FloodScopeMode Mode { get; }
    public Guid? RegionId { get; }
    public long Revision { get; }
}

/// <summary>A cache of a readback, never a queued instruction to restore preferences on reconnect.</summary>
public sealed class NodeRegionSnapshot
{
    public NodeRegionSnapshot(Guid nodeId, Guid? sessionId, long? generation, DateTimeOffset? observedUtc,
        RegionSupport support, NodeFloodScope value, RegionSnapshotFreshness freshness, long revision = 0)
    {
        RegionModelValidation.Identity(nodeId); ArgumentNullException.ThrowIfNull(value);
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (!Enum.IsDefined(support) || !Enum.IsDefined(freshness)) throw new ArgumentOutOfRangeException(nameof(support));
        if (sessionId == Guid.Empty || generation < 0) throw new ArgumentException("Invalid session identity.");
        if (freshness == RegionSnapshotFreshness.CurrentSession && (sessionId is null || generation is null))
            throw new ArgumentException("Current observations require session identity.");
        if (value.State != NodeFloodScopeState.Unknown &&
            (support != RegionSupport.Supported || sessionId is null || generation is null || observedUtc is null || freshness == RegionSnapshotFreshness.Unknown))
            throw new ArgumentException("Known preferences require supported, identified and dated observation.");
        NodeId = nodeId; SessionId = sessionId; Generation = generation; ObservedUtc = observedUtc?.ToUniversalTime();
        Support = support; Value = value; Freshness = freshness;
        Revision = revision;
    }
    public Guid NodeId { get; }
    public Guid? SessionId { get; }
    public long? Generation { get; }
    public DateTimeOffset? ObservedUtc { get; }
    public RegionSupport Support { get; }
    public NodeFloodScope Value { get; }
    public RegionSnapshotFreshness Freshness { get; }
    /// <summary>Publication revision within this session; offline cache does not carry session revisions.</summary>
    public long Revision { get; }
    public bool IsCurrent(Guid sessionId, long generation) => Freshness == RegionSnapshotFreshness.CurrentSession && SessionId == sessionId && Generation == generation;
    public override string ToString() => $"Node region observation: {NodeId}, {Support}, {Value.State}, {Freshness}";
}

/// <summary>Discovery evidence remains separate from saved definitions; no key is derived from a private name.</summary>
public sealed class RegionObservation
{
    private readonly byte[] _repeaterKey;
    public RegionObservation(Guid nodeId, ReadOnlySpan<byte> repeaterPublicKey, string protocolName,
        RegionObservationSource source, DateTimeOffset firstSeenUtc, DateTimeOffset lastSeenUtc)
    {
        RegionModelValidation.Identity(nodeId);
        if (repeaterPublicKey.Length != ProtocolLimits.PublicKeySize) throw new ArgumentException("Full repeater public key required.", nameof(repeaterPublicKey));
        ArgumentException.ThrowIfNullOrEmpty(protocolName);
        if (protocolName.IndexOfAny(['\0', ',']) >= 0 || new UTF8Encoding(false, true).GetByteCount(protocolName) > ProtocolLimits.MaxRegionNameUtf8Bytes)
            throw new ArgumentException("Invalid observed region name.", nameof(protocolName));
        if (!Enum.IsDefined(source) || lastSeenUtc < firstSeenUtc) throw new ArgumentOutOfRangeException(nameof(source));
        NodeId = nodeId; _repeaterKey = repeaterPublicKey.ToArray(); ProtocolName = protocolName; Source = source;
        FirstSeenUtc = firstSeenUtc.ToUniversalTime(); LastSeenUtc = lastSeenUtc.ToUniversalTime();
    }
    public Guid NodeId { get; }
    public byte[] GetRepeaterPublicKey() => (byte[])_repeaterKey.Clone();
    public string ProtocolName { get; }
    public RegionObservationSource Source { get; }
    public DateTimeOffset FirstSeenUtc { get; }
    public DateTimeOffset LastSeenUtc { get; }
}

/// <summary>Immutable attempt scope. It has no catalogue FK; deletion cannot rewrite old attempts.</summary>
public sealed class ResolvedRegionScope
{
    public ResolvedRegionScope(Guid nodeId, Guid? sessionId, long? generation, FloodScopeMode requestedMode,
        long policyRevision, StoredRegionScopeState state, RegionKey? key, string? name, int? maximumTextBytes)
    {
        RegionModelValidation.Identity(nodeId);
        if (!Enum.IsDefined(requestedMode) || !Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (sessionId == Guid.Empty || generation < 0 || policyRevision < 0 || maximumTextBytes < 0) throw new ArgumentOutOfRangeException(nameof(policyRevision));
        if (state == StoredRegionScopeState.KnownScoped && (key is null || key.IsEmpty) ||
            state != StoredRegionScopeState.KnownScoped && key is not null)
            throw new ArgumentException("Only a known scoped capture contains a nonzero key.", nameof(key));
        if (state is StoredRegionScopeState.KnownScoped or StoredRegionScopeState.KnownUnscoped && (sessionId is null || generation is null))
            throw new ArgumentException("Known send scope requires session identity.");
        if (requestedMode == FloodScopeMode.ForceUnscoped && state == StoredRegionScopeState.KnownScoped ||
            requestedMode == FloodScopeMode.ExplicitRegion && state == StoredRegionScopeState.KnownUnscoped)
            throw new ArgumentException("Requested policy contradicts the captured scope.");
        if (name is not null && (name.Contains('\0') || new UTF8Encoding(false, true).GetByteCount(name) > ProtocolLimits.MaxRegionNameUtf8Bytes))
            throw new ArgumentException("Invalid scope name snapshot.", nameof(name));
        NodeId = nodeId; SessionId = sessionId; Generation = generation; RequestedMode = requestedMode;
        PolicyRevision = policyRevision; State = state; Key = key; Name = name; MaximumTextBytes = maximumTextBytes;
    }
    public Guid NodeId { get; }
    public Guid? SessionId { get; }
    public long? Generation { get; }
    public FloodScopeMode RequestedMode { get; }
    public long PolicyRevision { get; }
    public StoredRegionScopeState State { get; }
    public RegionKey? Key { get; }
    public string? Name { get; }
    public int? MaximumTextBytes { get; }
    public string? KeyFingerprint => Key is null ? null : Convert.ToHexString(SHA256.HashData(Key.ToArray()));
    public bool CanReplayExactly => State is StoredRegionScopeState.KnownScoped or StoredRegionScopeState.KnownUnscoped;
    public override string ToString() => $"Send scope: {NodeId}, {RequestedMode}, {State}, revision {PolicyRevision}";
}

internal static class RegionModelValidation
{
    internal static void Identity(Guid id) { if (id == Guid.Empty) throw new ArgumentException("An existing node/entity identity is required.", nameof(id)); }
}
