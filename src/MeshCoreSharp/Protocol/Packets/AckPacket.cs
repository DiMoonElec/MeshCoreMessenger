using MeshCoreSharp.Models;

namespace MeshCoreSharp.Protocol.Packets;

public sealed class AckPacket : CompanionPacket
{
    internal AckPacket(ReadOnlyMemory<byte> frame, MessageAcknowledgement info)
        : base((byte)PacketType.Ack, frame) => Info = info;
    public MessageAcknowledgement Info { get; }
}
