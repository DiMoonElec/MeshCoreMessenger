using MeshCoreSharp.Models;

namespace MeshCoreSharp.Protocol.Packets;

public sealed class DeviceInfoPacket : CompanionPacket
{
    internal DeviceInfoPacket(ReadOnlyMemory<byte> rawFrame, DeviceInfo info)
        : base((byte)PacketType.DeviceInfo, rawFrame)
    {
        Info = info;
    }

    public DeviceInfo Info { get; }
}
