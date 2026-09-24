using MeshCoreSharp.Protocol.Encoding;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class ContactEndPacketParser : IPacketParser
{
    public PacketType Type => PacketType.ContactEnd;

    public CompanionPacket Parse(ReadOnlyMemory<byte> frame)
    {
        var reader = new PacketReader(frame.Span[1..]);
        return new ContactEndPacket(frame, reader.ReadUInt32LittleEndian());
    }
}
