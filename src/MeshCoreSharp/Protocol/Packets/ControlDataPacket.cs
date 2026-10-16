using MeshCoreSharp.Models;

namespace MeshCoreSharp.Protocol.Packets;

public sealed class ControlDataPacket : CompanionPacket
{
    private readonly byte[] _payload;
    internal ControlDataPacket(ReadOnlyMemory<byte> frame, double snr, sbyte rssi, byte pathDescriptor,
        byte[] payload, DiscoveredNeighbor? neighbor) : base((byte)PacketType.ControlData, frame)
    { ReceivedSnr = snr; ReceivedRssi = rssi; PathDescriptor = pathDescriptor; _payload = payload; Neighbor = neighbor; }
    public double ReceivedSnr { get; }
    public sbyte ReceivedRssi { get; }
    public byte PathDescriptor { get; }
    public DiscoveredNeighbor? Neighbor { get; }
    public byte[] GetPayload() => (byte[])_payload.Clone();
}
