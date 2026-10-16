using MeshCoreSharp.Protocol;

namespace MeshCoreSharp.Models;

/// <summary>An immutable 128-bit transport key. Key material is never printed.</summary>
public sealed class RegionKey : IEquatable<RegionKey>
{
    private readonly byte[] _bytes;

    public RegionKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ProtocolLimits.RegionKeySize)
            throw new ArgumentException("A region key must contain exactly 16 bytes.", nameof(bytes));
        _bytes = bytes.ToArray();
    }

    public bool IsEmpty => _bytes.All(b => b == 0);
    public byte[] ToArray() => (byte[])_bytes.Clone();
    public bool Equals(RegionKey? other) => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);
    public override bool Equals(object? obj) => obj is RegionKey other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in _bytes) hash.Add(value);
        return hash.ToHashCode();
    }
    public override string ToString() => "Region key (redacted)";
}
