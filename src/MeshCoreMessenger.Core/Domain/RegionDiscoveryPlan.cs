using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using System.Security.Cryptography;
using System.Text;

namespace MeshCoreMessenger.Core.Domain;

public enum RegionDiscoveryPreparation { ReadyDirect, RequiresContactCreation, RequiresTemporaryZeroHop, NoUsableReplyPath }
public enum RegionReplyPathEvidence { None, ObservedZeroHopNeighbor, CurrentZeroHopPath, AssumedReverse, Confirmed }

public sealed class ConfirmedRegionReplyPath
{
    private readonly byte[] _key;
    public ConfirmedRegionReplyPath(ReadOnlySpan<byte> publicKey, ReplyPath replyPath)
    {
        if (publicKey.Length != ProtocolLimits.PublicKeySize) throw new ArgumentException("Full repeater key required.", nameof(publicKey));
        ArgumentNullException.ThrowIfNull(replyPath); _key = publicKey.ToArray(); ReplyPath = replyPath;
    }
    public byte[] GetPublicKey() => (byte[])_key.Clone();
    public ReplyPath ReplyPath { get; }
}

/// <summary>A read-only preview. Creating a contact and temporary route preparation require explicit execution later.</summary>
public sealed class RegionDiscoveryPlan
{
    private readonly byte[] _key;
    private readonly Contact? _contact;
    internal RegionDiscoveryPlan(Guid nodeId, Guid sessionId, long generation, ReadOnlySpan<byte> key, Contact? contact,
        RegionDiscoveryPreparation preparation, ReplyPath? replyPath, RegionReplyPathEvidence evidence, DateTimeOffset capturedUtc, string? reason = null,
        long contactObservationVersion = 0)
    {
        NodeId = nodeId; SessionId = sessionId; Generation = generation; _key = key.ToArray();
        _contact = Copy(contact); Preparation = preparation; ReplyPath = replyPath; ReplyPathEvidence = evidence;
        CapturedUtc = capturedUtc; Reason = reason; ContactRevision = contact is null ? null : Revision(contact);
        ContactObservationVersion = contactObservationVersion;
    }
    public Guid NodeId { get; }
    public Guid SessionId { get; }
    public long Generation { get; }
    public DateTimeOffset CapturedUtc { get; }
    public RegionDiscoveryPreparation Preparation { get; }
    public ReplyPath? ReplyPath { get; }
    public RegionReplyPathEvidence ReplyPathEvidence { get; }
    public string? ContactRevision { get; }
    public long ContactObservationVersion { get; }
    public string? Reason { get; }
    public bool MayCreateContact => Preparation == RegionDiscoveryPreparation.RequiresContactCreation;
    public bool RequiresConditionalRestore => Preparation == RegionDiscoveryPreparation.RequiresTemporaryZeroHop;
    public byte[] GetPublicKey() => (byte[])_key.Clone();
    public Contact? GetContactSnapshot() => Copy(_contact);
    public bool MatchesOwner(Guid nodeId, Guid sessionId, long generation) => NodeId == nodeId && SessionId == sessionId && Generation == generation;
    public bool MatchesContact(Contact contact) => ContactRevision is not null && ContactRevision == Revision(contact);
    private static Contact? Copy(Contact? contact) => contact is null ? null : contact with { PublicKey = contact.PublicKey.ToArray(), OutPath = contact.OutPath.ToArray() };
    private static string Revision(Contact contact)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(contact.PublicKey.Span); writer.Write((byte)contact.AdvertisementType); writer.Write(contact.Flags);
        writer.Write(contact.OutPathLength); writer.Write(contact.OutPath.Span); writer.Write(contact.Name);
        writer.Write(contact.LastAdvertTimestamp); writer.Write(contact.LastModified);
        writer.Write(contact.AdvertisementLatitude); writer.Write(contact.AdvertisementLongitude); writer.Flush();
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }
    public override string ToString() => $"Region discovery: {Preparation}, {ReplyPathEvidence}, generation {Generation}";
}

public sealed record RegionDiscoveryPreflightResult(IReadOnlyList<RegionDiscoveryPlan> Plans, int UnresolvedPrefixCount, int OmittedTargetCount);
