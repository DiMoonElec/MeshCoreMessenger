using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol.Encoding;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class AdvertisementPacketParser(PacketType type) : IPacketParser
{
    public PacketType Type { get; } = type is PacketType.Advertisement or PacketType.NewAdvertisement
        ? type : throw new ArgumentOutOfRangeException(nameof(type));

    public CompanionPacket Parse(ReadOnlyMemory<byte> frame)
    {
        if (Type == PacketType.NewAdvertisement)
        {
            var contact = ContactPacketParser.ReadContact(frame.Span[1..]);
            return new AdvertisementPacket(Type, frame, new AdvertisementInfo(contact.PublicKey, contact));
        }
        var reader = new PacketReader(frame.Span);
        reader.Skip(1);
        return new AdvertisementPacket(Type, frame,
            new AdvertisementInfo(reader.ReadBytes(ProtocolLimits.PublicKeySize), null));
    }
}
