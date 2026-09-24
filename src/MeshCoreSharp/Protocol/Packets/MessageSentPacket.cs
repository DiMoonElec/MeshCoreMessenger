using MeshCoreSharp.Models;

namespace MeshCoreSharp.Protocol.Packets;

public sealed class MessageSentPacket : CompanionPacket
{
    internal MessageSentPacket(ReadOnlyMemory<byte> frame, MessageSentInfo info)
        : base((byte)PacketType.MessageSent, frame) => Info = info;
    public MessageSentInfo Info { get; }
}
