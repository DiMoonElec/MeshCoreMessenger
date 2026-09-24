using System.Buffers.Binary;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class OkPacketParser : IPacketParser
{
    public PacketType Type => PacketType.Ok;

    public CompanionPacket Parse(ReadOnlyMemory<byte> frame)
    {
        uint? value = frame.Length >= 5
            ? BinaryPrimitives.ReadUInt32LittleEndian(frame.Span.Slice(1, 4))
            : null;
        return new OkPacket(frame, value);
    }
}
