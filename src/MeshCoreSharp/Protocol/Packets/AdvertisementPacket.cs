using MeshCoreSharp.Models;

namespace MeshCoreSharp.Protocol.Packets;

/// <summary>ADVERT or NEW_ADVERT push; distinct from a GET_CONTACTS response.</summary>
public sealed class AdvertisementPacket : CompanionPacket
{
    internal AdvertisementPacket(PacketType type, ReadOnlyMemory<byte> frame, AdvertisementInfo info)
        : base((byte)type, frame) => Info = info;
    public AdvertisementInfo Info { get; }
}
