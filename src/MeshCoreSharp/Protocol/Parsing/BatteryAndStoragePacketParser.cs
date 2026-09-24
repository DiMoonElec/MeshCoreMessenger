using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol.Encoding;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class BatteryAndStoragePacketParser : IPacketParser
{
    public PacketType Type => PacketType.BatteryAndStorage;

    public CompanionPacket Parse(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length < 3)
            throw new MeshCoreProtocolException($"BATTERY frame is too short: {frame.Length} bytes.");

        var reader = new PacketReader(frame.Span[1..]);
        var millivolts = reader.ReadUInt16LittleEndian();

        uint? usedKb = null;
        uint? totalKb = null;
        if (frame.Length >= 11)
        {
            usedKb = reader.ReadUInt32LittleEndian();
            totalKb = reader.ReadUInt32LittleEndian();
        }

        return new BatteryAndStoragePacket(
            frame,
            new BatteryAndStorageInfo(millivolts, usedKb, totalKb));
    }
}
