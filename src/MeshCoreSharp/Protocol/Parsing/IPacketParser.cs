using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal interface IPacketParser
{
    PacketType Type { get; }
    CompanionPacket Parse(ReadOnlyMemory<byte> frame);
}
