using MeshCoreSharp.Models;

namespace MeshCoreSharp.Protocol.Packets;

public sealed class BatteryAndStoragePacket : CompanionPacket
{
    internal BatteryAndStoragePacket(ReadOnlyMemory<byte> rawFrame, BatteryAndStorageInfo info)
        : base((byte)PacketType.BatteryAndStorage, rawFrame)
    {
        Info = info;
    }

    public BatteryAndStorageInfo Info { get; }
}
