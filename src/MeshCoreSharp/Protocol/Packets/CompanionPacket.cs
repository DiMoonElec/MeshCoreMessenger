namespace MeshCoreSharp.Protocol.Packets;

public abstract class CompanionPacket
{
    protected CompanionPacket(byte rawType, ReadOnlyMemory<byte> rawFrame)
    {
        RawType = rawType;
        RawFrame = rawFrame;
    }

    public byte RawType { get; }
    public PacketType Type => (PacketType)RawType;
    public bool IsPush => RawType >= 0x80;
    public ReadOnlyMemory<byte> RawFrame { get; }
}
