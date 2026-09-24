using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol.Encoding;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class MessageQueuePacketParser(PacketType type) : IPacketParser
{
    public PacketType Type => type;

    public CompanionPacket Parse(ReadOnlyMemory<byte> frame)
    {
        if (Type == PacketType.NoMoreMessages)
            return new NoMoreMessagesPacket(frame);

        var reader = new PacketReader(frame.Span[1..]);
        double? snr = null;
        if (Type is PacketType.ContactMessageReceivedV3 or PacketType.ChannelMessageReceivedV3 or PacketType.ChannelDataReceived)
        {
            snr = unchecked((sbyte)reader.ReadByte()) / 4d;
            reader.Skip(2); // Reserved bytes; preserve the original frame for diagnostics.
        }

        ReceivedMessage message;
        if (Type is PacketType.ContactMessageReceived or PacketType.ContactMessageReceivedV3)
        {
            var contactPrefix = reader.ReadBytes(ProtocolLimits.MessageContactPrefixSize);
            var pathLength = reader.ReadByte();
            var textType = (MessageTextType)reader.ReadByte();
            var timestamp = DateTimeOffset.FromUnixTimeSeconds(reader.ReadUInt32LittleEndian());
            var senderPrefix = textType == MessageTextType.SignedPlain
                ? reader.ReadBytes(ProtocolLimits.MessageSenderPrefixSize)
                : Array.Empty<byte>();
            message = new ContactMessage(contactPrefix, pathLength, textType, timestamp,
                reader.ReadUtf8ToEnd(trimWhitespace: false), senderPrefix, snr);
        }
        else if (Type == PacketType.ChannelDataReceived)
        {
            var channel = reader.ReadByte();
            var pathLength = reader.ReadByte();
            var dataType = reader.ReadUInt16LittleEndian();
            var length = reader.ReadByte();
            message = new ChannelDataMessage(channel, pathLength, dataType, reader.ReadBytes(length), snr);
        }
        else
        {
            var channel = reader.ReadByte();
            var pathLength = reader.ReadByte();
            var textType = (MessageTextType)reader.ReadByte();
            var timestamp = DateTimeOffset.FromUnixTimeSeconds(reader.ReadUInt32LittleEndian());
            message = new ChannelMessage(channel, pathLength, textType, timestamp,
                reader.ReadUtf8ToEnd(trimWhitespace: false), snr);
        }

        return new ReceivedMessagePacket(Type, frame, message);
    }
}
