namespace MeshCoreSharp.Protocol.Packets;

public sealed class OkPacket : CompanionPacket
{
    internal OkPacket(ReadOnlyMemory<byte> rawFrame, uint? value)
        : base((byte)PacketType.Ok, rawFrame)
    {
        Value = value;
    }

    public uint? Value { get; }
}
