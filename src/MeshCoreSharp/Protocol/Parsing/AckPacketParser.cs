using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol.Encoding;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class AckPacketParser : IPacketParser
{
    public PacketType Type => PacketType.Ack;
    public CompanionPacket Parse(ReadOnlyMemory<byte> frame)
    {
        var reader = new PacketReader(frame.Span);
        reader.Skip(1);
        return new AckPacket(frame, new MessageAcknowledgement(
            reader.ReadUInt32LittleEndian(), reader.ReadUInt32LittleEndian()));
    }
}
