using System.Buffers.Binary;
using System.Text;
using MeshCoreSharp.Protocol;

internal static class MessageFixtures
{
    public static byte[] Text(PacketType type, string text = "  Привет!\n", byte textType = 0)
    {
        var v3 = type is PacketType.ContactMessageReceivedV3 or PacketType.ChannelMessageReceivedV3;
        var contact = type is PacketType.ContactMessageReceived or PacketType.ContactMessageReceivedV3;
        var start = v3 ? 4 : 1;
        var headerSize = start + (contact ? 6 : 1) + 6;
        var extra = contact && textType == 2 ? 4 : 0;
        var bytes = Encoding.UTF8.GetBytes(text);
        var frame = new byte[headerSize + extra + bytes.Length];
        frame[0] = (byte)type;
        if (v3)
        {
            frame[1] = 0xE3; // -29 / 4 = -7.25 dB
            frame[2] = 0xAA; // Reserved bytes may be nonzero.
            frame[3] = 0xBB;
        }
        if (contact)
        {
            Convert.FromHexString("0123456789AB").CopyTo(frame, start);
            start += 6;
        }
        else frame[start++] = 7;
        frame[start++] = 0xFF;
        frame[start++] = textType;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(start), 1_700_000_123);
        start += 4;
        if (extra != 0)
        {
            Convert.FromHexString("DEADBEEF").CopyTo(frame, start);
            start += 4;
        }
        bytes.CopyTo(frame, start);
        return frame;
    }

    public static byte[] Data() => [0x1B, 0xE3, 0xAA, 0xBB, 7, 0x82, 0x34, 0x12, 3, 0x00, 0xFF, 0x0A];
}
