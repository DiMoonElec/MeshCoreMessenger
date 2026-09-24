using System.Buffers.Binary;
using System.Text;
using MeshCoreSharp.Protocol;

internal static class Fixtures
{
    public static byte[] Start(uint count) => Number(PacketType.ContactStart, count);
    public static byte[] End(uint lastModified = 1_700_000_123) => Number(PacketType.ContactEnd, lastModified);

    public static byte[] Number(PacketType type, uint value)
    {
        var frame = new byte[5];
        frame[0] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), value);
        return frame;
    }

    // Fixed wire offsets, independent of the production reader/writer.
    public static byte[] Contact(string name = "Узел", byte key = 0xA5)
    {
        var frame = new byte[148];
        frame[0] = 0x03;
        frame.AsSpan(1, 32).Fill(key);
        frame[33] = 2;
        frame[34] = 0x81; // Preserve unknown flag bits.
        frame[35] = 0x82; // Two hops, three bytes per path hash.
        frame.AsSpan(36, 64).Fill(0x5A);
        Encoding.UTF8.GetBytes(name).CopyTo(frame.AsSpan(100, 32));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(132), 1_700_000_000);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(136), -33_865_143);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(140), 151_209_900);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(144), 1_700_000_123);
        return frame;
    }
}
