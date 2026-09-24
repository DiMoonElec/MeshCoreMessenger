using System.Buffers.Binary;
using MeshCoreSharp.Exceptions;

namespace MeshCoreSharp.Protocol.Encoding;

internal ref struct PacketReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _offset;

    public PacketReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _offset = 0;
    }

    public int Remaining => _buffer.Length - _offset;
    public int Offset => _offset;

    public byte ReadByte()
    {
        EnsureAvailable(1);
        return _buffer[_offset++];
    }

    public ushort ReadUInt16LittleEndian()
    {
        EnsureAvailable(2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_offset, 2));
        _offset += 2;
        return value;
    }

    public uint ReadUInt32LittleEndian()
    {
        EnsureAvailable(4);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(_offset, 4));
        _offset += 4;
        return value;
    }

    public int ReadInt32LittleEndian()
    {
        EnsureAvailable(4);
        var value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_offset, 4));
        _offset += 4;
        return value;
    }

    public byte[] ReadBytes(int count)
    {
        EnsureAvailable(count);
        var result = _buffer.Slice(_offset, count).ToArray();
        _offset += count;
        return result;
    }

    public string ReadFixedUtf8(int count)
    {
        EnsureAvailable(count);
        var bytes = _buffer.Slice(_offset, count);
        _offset += count;

        var nul = bytes.IndexOf((byte)0);
        if (nul >= 0)
            bytes = bytes[..nul];

        return System.Text.Encoding.UTF8.GetString(bytes).Trim();
    }

    public string ReadUtf8ToEnd()
    {
        var bytes = _buffer[_offset..];
        _offset = _buffer.Length;

        var nul = bytes.IndexOf((byte)0);
        if (nul >= 0)
            bytes = bytes[..nul];

        return System.Text.Encoding.UTF8.GetString(bytes).Trim();
    }

    public void Skip(int count)
    {
        EnsureAvailable(count);
        _offset += count;
    }

    private void EnsureAvailable(int count)
    {
        if (count < 0 || Remaining < count)
        {
            throw new MeshCoreProtocolException(
                $"Malformed companion packet: need {count} byte(s) at offset {_offset}, but only {Remaining} remain.");
        }
    }
}
