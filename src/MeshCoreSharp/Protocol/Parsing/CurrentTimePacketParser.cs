using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Protocol.Encoding;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class CurrentTimePacketParser : IPacketParser
{
    public PacketType Type => PacketType.CurrentTime;

    public CompanionPacket Parse(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length < 5)
            throw new MeshCoreProtocolException($"CURRENT_TIME frame is too short: {frame.Length} bytes.");

        var reader = new PacketReader(frame.Span[1..]);
        var unixSeconds = reader.ReadUInt32LittleEndian();
        return new CurrentTimePacket(frame, DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
    }
}
