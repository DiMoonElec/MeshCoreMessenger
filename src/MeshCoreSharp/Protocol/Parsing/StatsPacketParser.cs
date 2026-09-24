using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol.Encoding;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class StatsPacketParser : IPacketParser
{
    public PacketType Type => PacketType.Stats;

    public CompanionPacket Parse(ReadOnlyMemory<byte> frame)
    {
        var reader = new PacketReader(frame.Span);
        reader.Skip(1);
        var subtype = (StatsType)reader.ReadByte();
        return subtype switch
        {
            StatsType.Core => new CoreStatsPacket(frame, new CoreStats(
                reader.ReadUInt16LittleEndian(), reader.ReadUInt32LittleEndian(),
                reader.ReadUInt16LittleEndian(), reader.ReadByte())),
            StatsType.Radio => new RadioStatsPacket(frame, new RadioStats(
                unchecked((short)reader.ReadUInt16LittleEndian()), unchecked((sbyte)reader.ReadByte()),
                unchecked((sbyte)reader.ReadByte()) / 4.0,
                reader.ReadUInt32LittleEndian(), reader.ReadUInt32LittleEndian())),
            StatsType.Packets => new PacketStatsPacket(frame, new PacketStats(
                reader.ReadUInt32LittleEndian(), reader.ReadUInt32LittleEndian(),
                reader.ReadUInt32LittleEndian(), reader.ReadUInt32LittleEndian(),
                reader.ReadUInt32LittleEndian(), reader.ReadUInt32LittleEndian(), reader.ReadUInt32LittleEndian())),
            _ => new RawCompanionPacket((byte)Type, frame),
        };
    }
}
