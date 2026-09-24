using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class ErrorPacketParser : IPacketParser
{
    public PacketType Type => PacketType.Error;

    public CompanionPacket Parse(ReadOnlyMemory<byte> frame)
    {
        MeshCoreErrorCode? code = frame.Length >= 2
            ? (MeshCoreErrorCode)frame.Span[1]
            : null;
        return new ErrorPacket(frame, code);
    }
}
