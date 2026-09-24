using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol.Encoding;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class DeviceInfoPacketParser : IPacketParser
{
    public PacketType Type => PacketType.DeviceInfo;

    public CompanionPacket Parse(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length < 2)
            throw new MeshCoreProtocolException($"DEVICE_INFO frame is too short: {frame.Length} bytes.");

        var reader = new PacketReader(frame.Span[1..]);
        var firmwareVersion = reader.ReadByte();

        int? maxContacts = null;
        byte? maxChannels = null;
        uint? blePin = null;
        string? firmwareBuild = null;
        string? model = null;
        string? semanticVersion = null;
        bool? repeaterMode = null;
        byte? pathHashMode = null;

        if (firmwareVersion >= 3)
        {
            if (reader.Remaining < 78)
            {
                throw new MeshCoreProtocolException(
                    $"DEVICE_INFO v{firmwareVersion} is truncated: expected at least 80 total bytes, got {frame.Length}.");
            }

            maxContacts = reader.ReadByte() * 2;
            maxChannels = reader.ReadByte();
            blePin = reader.ReadUInt32LittleEndian();
            firmwareBuild = reader.ReadFixedUtf8(12);
            model = reader.ReadFixedUtf8(40);
            semanticVersion = reader.ReadFixedUtf8(20);
        }

        // meshcore_py intentionally parses these fields defensively because
        // devices have existed with short versioned responses.
        if (firmwareVersion >= 9 && reader.Remaining >= 1)
            repeaterMode = reader.ReadByte() != 0;

        if (firmwareVersion >= 10 && reader.Remaining >= 1)
            pathHashMode = reader.ReadByte();

        var info = new DeviceInfo(
            firmwareVersion,
            maxContacts,
            maxChannels,
            blePin,
            firmwareBuild,
            model,
            semanticVersion,
            repeaterMode,
            pathHashMode);

        return new DeviceInfoPacket(frame, info);
    }
}
