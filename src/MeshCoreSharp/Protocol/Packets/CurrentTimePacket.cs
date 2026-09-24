namespace MeshCoreSharp.Protocol.Packets;

public sealed class CurrentTimePacket : CompanionPacket
{
    internal CurrentTimePacket(ReadOnlyMemory<byte> rawFrame, DateTimeOffset value)
        : base((byte)PacketType.CurrentTime, rawFrame)
    {
        Value = value;
    }

    public DateTimeOffset Value { get; }
}
