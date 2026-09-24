using MeshCoreSharp.Protocol.Encoding;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class ContactStartPacketParser : IPacketParser
{
    public PacketType Type => PacketType.ContactStart;

    public CompanionPacket Parse(ReadOnlyMemory<byte> frame)
    {
        var reader = new PacketReader(frame.Span[1..]);
        return new ContactStartPacket(frame, reader.ReadUInt32LittleEndian());
    }
}
