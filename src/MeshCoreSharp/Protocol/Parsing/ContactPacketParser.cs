using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol.Encoding;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class ContactPacketParser : IPacketParser
{
    public PacketType Type => PacketType.Contact;

    public CompanionPacket Parse(ReadOnlyMemory<byte> frame)
    {
        var reader = new PacketReader(frame.Span[1..]);
        var contact = new Contact(
            reader.ReadBytes(ProtocolLimits.PublicKeySize),
            (AdvertisementType)reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadBytes(ProtocolLimits.ContactPathSize),
            reader.ReadFixedUtf8(ProtocolLimits.ContactNameSize),
            reader.ReadUInt32LittleEndian(),
            reader.ReadInt32LittleEndian() / 1_000_000d,
            reader.ReadInt32LittleEndian() / 1_000_000d,
            reader.ReadUInt32LittleEndian());

        return new ContactPacket(frame, contact);
    }
}
