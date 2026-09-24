using System.Buffers;
using System.Buffers.Binary;

namespace MeshCoreSharp.Protocol.Encoding;

internal sealed class PacketWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    public int Length => _buffer.WrittenCount;

    public void WriteByte(byte value)
    {
        var span = _buffer.GetSpan(1);
        span[0] = value;
        _buffer.Advance(1);
    }

    public void WriteUInt16LittleEndian(ushort value)
    {
        var span = _buffer.GetSpan(2);
        BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        _buffer.Advance(2);
    }

    public void WriteUInt32LittleEndian(uint value)
    {
        var span = _buffer.GetSpan(4);
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        _buffer.Advance(4);
    }

    public void WriteInt32LittleEndian(int value)
    {
        var span = _buffer.GetSpan(4);
        BinaryPrimitives.WriteInt32LittleEndian(span, value);
        _buffer.Advance(4);
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        var span = _buffer.GetSpan(value.Length);
        value.CopyTo(span);
        _buffer.Advance(value.Length);
    }

    public void WriteUtf8(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteBytes(System.Text.Encoding.UTF8.GetBytes(value));
    }

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();
}
