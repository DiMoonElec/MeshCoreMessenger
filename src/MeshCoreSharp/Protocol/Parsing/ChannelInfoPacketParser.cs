using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol.Encoding;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class ChannelInfoPacketParser : IPacketParser
{
    public PacketType Type => PacketType.ChannelInfo;

    public CompanionPacket Parse(ReadOnlyMemory<byte> frame)
    {
        var reader = new PacketReader(frame.Span);
        reader.Skip(1);
        var index = reader.ReadByte();
        var name = reader.ReadFixedUtf8(ProtocolLimits.ChannelNameSize, trimWhitespace: false);
        var secret = reader.ReadBytes(ProtocolLimits.ChannelSecretSize);
        return new ChannelInfoPacket(frame, new ChannelInfo(index, name, secret));
    }
}
