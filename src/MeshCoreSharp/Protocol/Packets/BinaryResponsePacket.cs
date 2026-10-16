namespace MeshCoreSharp.Protocol.Packets;

/// <summary>A generic remote binary reply. Payload interpretation depends on the originating request.</summary>
public sealed class BinaryResponsePacket : CompanionPacket
{
    private readonly byte[] _payload;
    internal BinaryResponsePacket(ReadOnlyMemory<byte> frame, byte reserved, uint tag, byte[] payload)
        : base((byte)PacketType.BinaryResponse, frame) { Reserved = reserved; Tag = tag; _payload = payload; }
    public byte Reserved { get; }
    public uint Tag { get; }
    public byte[] GetPayload() => (byte[])_payload.Clone();
}
