using MeshCoreSharp.Protocol;

namespace MeshCoreSharp.Models;

/// <summary>A control-discovery observation. An eight-byte prefix cannot establish a node identity.</summary>
public sealed class DiscoveredNeighbor
{
    private readonly byte[] _key;
    internal DiscoveredNeighbor(AdvertisementType nodeType, uint tag, ReadOnlySpan<byte> key,
        double inboundSnr, double receivedSnr, sbyte receivedRssi, byte pathDescriptor)
    { NodeType = nodeType; Tag = tag; _key = key.ToArray(); InboundSnr = inboundSnr; ReceivedSnr = receivedSnr; ReceivedRssi = receivedRssi; PathDescriptor = pathDescriptor; }
    public AdvertisementType NodeType { get; }
    public uint Tag { get; }
    public bool HasFullPublicKey => _key.Length == ProtocolLimits.PublicKeySize;
    public byte[] GetKeyBytes() => (byte[])_key.Clone();
    public double InboundSnr { get; }
    public double ReceivedSnr { get; }
    public sbyte ReceivedRssi { get; }
    public byte PathDescriptor { get; }
}
