namespace MeshCoreSharp.Protocol.Packets;

public sealed class RawCompanionPacket : CompanionPacket
{
    internal RawCompanionPacket(byte rawType, ReadOnlyMemory<byte> rawFrame)
        : base(rawType, rawFrame)
    {
    }

    public ReadOnlyMemory<byte> Payload => RawFrame.Length > 1 ? RawFrame.Slice(1) : ReadOnlyMemory<byte>.Empty;
}
