namespace MeshCoreSharp.Protocol.Packets;

public sealed class ContactEndPacket : CompanionPacket
{
    internal ContactEndPacket(ReadOnlyMemory<byte> rawFrame, uint mostRecentLastModified)
        : base((byte)PacketType.ContactEnd, rawFrame) => MostRecentLastModified = mostRecentLastModified;

    public uint MostRecentLastModified { get; }
}
