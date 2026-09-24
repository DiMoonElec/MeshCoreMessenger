using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol.Encoding;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class MessageSentPacketParser : IPacketParser
{
    public PacketType Type => PacketType.MessageSent;
    public CompanionPacket Parse(ReadOnlyMemory<byte> frame)
    {
        var reader = new PacketReader(frame.Span);
        reader.Skip(1);
        var route = reader.ReadByte();
        if (route > 1) throw new MeshCoreProtocolException("MSG_SENT has an invalid route flag.");
        return new MessageSentPacket(frame, new MessageSentInfo(route == 1,
            reader.ReadUInt32LittleEndian(), reader.ReadUInt32LittleEndian()));
    }
}
