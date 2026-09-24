using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class CompanionPacketDecoder
{
    private readonly IReadOnlyDictionary<PacketType, IPacketParser> _parsers;

    public CompanionPacketDecoder()
    {
        IPacketParser[] parsers =
        [
            new OkPacketParser(),
            new ErrorPacketParser(),
            new SelfInfoPacketParser(),
            new CurrentTimePacketParser(),
            new BatteryAndStoragePacketParser(),
            new DeviceInfoPacketParser(),
            new ContactStartPacketParser(),
            new ContactPacketParser(),
            new ContactEndPacketParser(),
            new MessageQueuePacketParser(PacketType.ContactMessageReceived),
            new MessageQueuePacketParser(PacketType.ContactMessageReceivedV3),
            new MessageQueuePacketParser(PacketType.ChannelMessageReceived),
            new MessageQueuePacketParser(PacketType.ChannelMessageReceivedV3),
            new MessageQueuePacketParser(PacketType.ChannelDataReceived),
            new MessageQueuePacketParser(PacketType.NoMoreMessages),
        ];

        _parsers = parsers.ToDictionary(parser => parser.Type);
    }

    public CompanionPacket Decode(ReadOnlyMemory<byte> frame)
    {
        if (frame.IsEmpty)
            throw new MeshCoreProtocolException("Received an empty companion frame.");

        var rawType = frame.Span[0];
        var type = (PacketType)rawType;

        if (_parsers.TryGetValue(type, out var parser))
            return parser.Parse(frame);

        return new RawCompanionPacket(rawType, frame);
    }
}
