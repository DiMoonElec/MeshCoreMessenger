using System.Buffers.Binary;

namespace MeshCoreSharp.Transport.Framing;

internal static class StreamFrameEncoder
{
    public const byte AppToCompanionMarker = 0x3C; // '<'
    public const byte CompanionToAppMarker = 0x3E; // '>'

    public static byte[] Encode(ReadOnlySpan<byte> payload, byte marker)
    {
        if (payload.Length is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(payload));

        var frame = new byte[3 + payload.Length];
        frame[0] = marker;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1, 2), checked((ushort)payload.Length));
        payload.CopyTo(frame.AsSpan(3));
        return frame;
    }
}
