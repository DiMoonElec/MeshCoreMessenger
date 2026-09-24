namespace MeshCoreSharp.Protocol.Packets;

public sealed class ErrorPacket : CompanionPacket
{
    internal ErrorPacket(ReadOnlyMemory<byte> rawFrame, MeshCoreErrorCode? errorCode)
        : base((byte)PacketType.Error, rawFrame)
    {
        ErrorCode = errorCode;
    }

    public MeshCoreErrorCode? ErrorCode { get; }
}
