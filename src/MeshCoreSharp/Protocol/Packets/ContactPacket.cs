using MeshCoreSharp.Models;

namespace MeshCoreSharp.Protocol.Packets;

public sealed class ContactPacket : CompanionPacket
{
    internal ContactPacket(ReadOnlyMemory<byte> rawFrame, Contact contact)
        : base((byte)PacketType.Contact, rawFrame) => Contact = contact;

    public Contact Contact { get; }
}
