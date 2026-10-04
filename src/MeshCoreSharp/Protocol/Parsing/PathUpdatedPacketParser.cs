using MeshCoreSharp.Protocol.Encoding;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class PathUpdatedPacketParser : IPacketParser
{
    public PacketType Type => PacketType.PathUpdated;

    public CompanionPacket Parse(ReadOnlyMemory<byte> frame)
    {
        var reader = new PacketReader(frame.Span);
        reader.Skip(1);
        return new PathUpdatedPacket(frame, reader.ReadBytes(ProtocolLimits.PublicKeySize));
    }
}
