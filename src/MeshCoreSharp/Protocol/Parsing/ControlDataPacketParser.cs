using System.Buffers.Binary;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class ControlDataPacketParser : IPacketParser
{
    public PacketType Type => PacketType.ControlData;
    public CompanionPacket Parse(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length is < 5 or > ProtocolLimits.CurrentFirmwareMaxFrameSize)
            throw new MeshCoreProtocolException("Invalid control response length.");
        var bytes = frame.Span;
        var payload = bytes[4..];
        var snr = (sbyte)bytes[1] / 4.0;
        var rssi = (sbyte)bytes[2];
        var descriptor = bytes[3];
        var hashSize = (descriptor >> 6) + 1;
        if (hashSize > 3 || (descriptor & 63) * hashSize > ProtocolLimits.ContactPathSize)
            throw new MeshCoreProtocolException("Invalid control path descriptor.");
        DiscoveredNeighbor? neighbor = null;
        if ((payload[0] & RegionProtocol.ControlTypeMask) == RegionProtocol.DiscoverResponse)
        {
            var keyLength = payload.Length - RegionProtocol.DiscoveryResponseHeaderSize;
            if (keyLength is not (ProtocolLimits.PublicKeySize or RegionProtocol.DiscoveryPrefixSize))
                throw new MeshCoreProtocolException("A discovery reply requires a full public key or an eight-byte prefix.");
            neighbor = new DiscoveredNeighbor((AdvertisementType)(payload[0] & RegionProtocol.NodeTypeMask),
                BinaryPrimitives.ReadUInt32LittleEndian(payload[2..]), payload[6..], (sbyte)payload[1] / 4.0, snr, rssi, descriptor);
        }
        return new ControlDataPacket(frame, snr, rssi, descriptor, payload.ToArray(), neighbor);
    }
}
