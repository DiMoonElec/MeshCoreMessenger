using MeshCoreSharp.Models;

namespace MeshCoreSharp.Protocol.Packets;

public abstract class StatsPacket : CompanionPacket
{
    private protected StatsPacket(ReadOnlyMemory<byte> rawFrame, StatsType statsType)
        : base((byte)PacketType.Stats, rawFrame) => StatsType = statsType;

    public StatsType StatsType { get; }
}

public sealed class CoreStatsPacket : StatsPacket
{
    internal CoreStatsPacket(ReadOnlyMemory<byte> rawFrame, CoreStats info)
        : base(rawFrame, StatsType.Core) => Info = info;
    public CoreStats Info { get; }
}

public sealed class RadioStatsPacket : StatsPacket
{
    internal RadioStatsPacket(ReadOnlyMemory<byte> rawFrame, RadioStats info)
        : base(rawFrame, StatsType.Radio) => Info = info;
    public RadioStats Info { get; }
}

public sealed class PacketStatsPacket : StatsPacket
{
    internal PacketStatsPacket(ReadOnlyMemory<byte> rawFrame, PacketStats info)
        : base(rawFrame, StatsType.Packets) => Info = info;
    public PacketStats Info { get; }
}
