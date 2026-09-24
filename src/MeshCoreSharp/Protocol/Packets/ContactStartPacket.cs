namespace MeshCoreSharp.Protocol.Packets;

public sealed class ContactStartPacket : CompanionPacket
{
    internal ContactStartPacket(ReadOnlyMemory<byte> rawFrame, uint totalCount)
        : base((byte)PacketType.ContactStart, rawFrame) => TotalCount = totalCount;

    /// <summary>Total contacts on the device, not a guaranteed stream item count.</summary>
    public uint TotalCount { get; }
}
