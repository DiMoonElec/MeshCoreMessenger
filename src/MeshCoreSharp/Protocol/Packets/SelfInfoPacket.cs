using MeshCoreSharp.Models;

namespace MeshCoreSharp.Protocol.Packets;

public sealed class SelfInfoPacket : CompanionPacket
{
    internal SelfInfoPacket(ReadOnlyMemory<byte> rawFrame, SelfInfo info)
        : base((byte)PacketType.SelfInfo, rawFrame)
    {
        Info = info;
    }

    public SelfInfo Info { get; }
}
