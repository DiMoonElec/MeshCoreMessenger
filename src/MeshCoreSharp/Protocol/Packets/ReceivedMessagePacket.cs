using MeshCoreSharp.Models;

namespace MeshCoreSharp.Protocol.Packets;

public sealed class ReceivedMessagePacket : MessageQueuePacket
{
    internal ReceivedMessagePacket(PacketType type, ReadOnlyMemory<byte> rawFrame, ReceivedMessage message)
        : base(type, rawFrame) => Message = message;

    public ReceivedMessage Message { get; }
}
