using MeshCoreSharp.Protocol;

namespace MeshCoreSharp.Models;

/// <summary>A packed direct reply path. Hashes are ordered by hop; prefixes are not public keys.</summary>
public sealed class ReplyPath
{
    private readonly byte[] _bytes;
    public byte Descriptor { get; }
    public int HashSize => (Descriptor >> 6) + 1;
    public int HopCount => Descriptor & 63;
    public static ReplyPath ZeroHop { get; } = new(0, []);

    public ReplyPath(byte descriptor, ReadOnlySpan<byte> bytes)
    {
        Descriptor = descriptor;
        if (HashSize > 3 || HopCount * HashSize > ProtocolLimits.ContactPathSize || bytes.Length != HopCount * HashSize)
            throw new ArgumentException("Invalid packed direct path descriptor or byte count.", nameof(bytes));
        _bytes = bytes.ToArray();
    }
    public byte[] ToArray() => (byte[])_bytes.Clone();
    public ReplyPath ReverseHops()
    {
        var reversed = new byte[_bytes.Length];
        for (var hop = 0; hop < HopCount; hop++)
            _bytes.AsSpan(hop * HashSize, HashSize).CopyTo(reversed.AsSpan((HopCount - hop - 1) * HashSize));
        return new ReplyPath(Descriptor, reversed);
    }
}
