using MeshCoreSharp.Models;

namespace MeshCoreSharp.Protocol.Packets;

public sealed class ChannelInfoPacket : CompanionPacket
{
    internal ChannelInfoPacket(ReadOnlyMemory<byte> rawFrame, ChannelInfo info)
        : base((byte)PacketType.ChannelInfo, rawFrame) => Info = info;

    public ChannelInfo Info { get; }
}
