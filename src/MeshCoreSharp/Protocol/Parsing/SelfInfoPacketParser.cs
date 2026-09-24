using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol.Encoding;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class SelfInfoPacketParser : IPacketParser
{
    public PacketType Type => PacketType.SelfInfo;

    public CompanionPacket Parse(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length < 58)
            throw new MeshCoreProtocolException($"SELF_INFO frame is too short: {frame.Length} bytes.");

        var reader = new PacketReader(frame.Span[1..]);
        var advertisementType = (AdvertisementType)reader.ReadByte();
        var txPower = reader.ReadByte();
        var maxTxPower = reader.ReadByte();
        var publicKey = reader.ReadBytes(32);
        var latitude = reader.ReadInt32LittleEndian() / 1_000_000d;
        var longitude = reader.ReadInt32LittleEndian() / 1_000_000d;
        var multiAcks = reader.ReadByte();
        var locationPolicy = reader.ReadByte();
        var telemetryMode = reader.ReadByte();
        var manualAddContacts = reader.ReadByte() != 0;
        var frequencyMhz = reader.ReadUInt32LittleEndian() / 1000d;
        var bandwidthKhz = reader.ReadUInt32LittleEndian() / 1000d;
        var spreadingFactor = reader.ReadByte();
        var codingRate = reader.ReadByte();
        var name = reader.ReadUtf8ToEnd(trimWhitespace: false);

        var info = new SelfInfo(
            advertisementType,
            txPower,
            maxTxPower,
            publicKey,
            latitude,
            longitude,
            multiAcks,
            locationPolicy,
            telemetryMode,
            manualAddContacts,
            frequencyMhz,
            bandwidthKhz,
            spreadingFactor,
            codingRate,
            name);

        return new SelfInfoPacket(frame, info);
    }
}
