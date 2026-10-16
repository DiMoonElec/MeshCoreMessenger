using System.Buffers.Binary;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class BinaryResponsePacketParser : IPacketParser
{
    public PacketType Type => PacketType.BinaryResponse;
    public CompanionPacket Parse(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length is < 6 or > ProtocolLimits.CurrentFirmwareMaxFrameSize)
            throw new MeshCoreProtocolException("Invalid binary response header or frame length.");
        return new BinaryResponsePacket(frame, frame.Span[1], BinaryPrimitives.ReadUInt32LittleEndian(frame.Span[2..]), frame.Span[6..].ToArray());
    }
}
