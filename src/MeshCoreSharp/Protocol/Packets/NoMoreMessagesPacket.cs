namespace MeshCoreSharp.Protocol.Packets;

public sealed class NoMoreMessagesPacket : MessageQueuePacket
{
    internal NoMoreMessagesPacket(ReadOnlyMemory<byte> rawFrame)
        : base(PacketType.NoMoreMessages, rawFrame) { }
}
