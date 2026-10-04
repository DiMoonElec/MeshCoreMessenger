namespace MeshCoreSharp.Protocol.Packets;

/// <summary>The Companion changed a contact route. Read the contact for its current path.</summary>
public sealed class PathUpdatedPacket : CompanionPacket
{
    internal PathUpdatedPacket(ReadOnlyMemory<byte> frame, ReadOnlyMemory<byte> publicKey)
        : base((byte)PacketType.PathUpdated, frame) => PublicKey = publicKey;

    public ReadOnlyMemory<byte> PublicKey { get; }
}
