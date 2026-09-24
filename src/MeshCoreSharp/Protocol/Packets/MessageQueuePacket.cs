namespace MeshCoreSharp.Protocol.Packets;

/// <summary>A response to SYNC_NEXT_MESSAGE.</summary>
public abstract class MessageQueuePacket : CompanionPacket
{
    private protected MessageQueuePacket(PacketType type, ReadOnlyMemory<byte> rawFrame)
        : base((byte)type, rawFrame) { }

    internal static bool IsResponseType(PacketType type) => type is
        PacketType.ContactMessageReceived or PacketType.ContactMessageReceivedV3 or
        PacketType.ChannelMessageReceived or PacketType.ChannelMessageReceivedV3 or
        PacketType.ChannelDataReceived or PacketType.NoMoreMessages;
}
