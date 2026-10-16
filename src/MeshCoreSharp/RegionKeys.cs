using System.Security.Cryptography;
using System.Text;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;

namespace MeshCoreSharp;

/// <summary>Public region derivation, independent of channel secrets and application storage.</summary>
public static class RegionKeys
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Hashes one leading '#' and the exact name, preserving case and Unicode bytes.</summary>
    public static RegionKey DerivePublic(string name)
    {
        var normalized = NormalizePublicName(name);
        return new RegionKey(SHA256.HashData(StrictUtf8.GetBytes("#" + normalized)).AsSpan(0, ProtocolLimits.RegionKeySize));
    }

    /// <summary>Removes at most one service prefix and validates the RegionMap name.</summary>
    public static string NormalizePublicName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var normalized = name[0] == '#' ? name[1..] : name;
        if (normalized.Length == 0 || normalized[0] == '$' || normalized.Contains('#'))
            throw new ArgumentException("Expected a public region name with at most one leading '#'.", nameof(name));
        ValidateName(normalized);
        return normalized;
    }

    internal static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var bytes = StrictUtf8.GetBytes(name);
        if (bytes.Length > ProtocolLimits.MaxRegionNameUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(name), "Region names must fit in 30 UTF-8 bytes without truncation.");
        // RegionMap::is_name_char, pinned firmware 81dbae59. Validate bytes, not .NET letters.
        if (bytes.Any(b => !(b == '-' || b == '$' || b == '#' || b >= '0' && b <= '9' || b >= 'A')))
            throw new ArgumentException("Region name contains a character unsupported by RegionMap.", nameof(name));
    }
}
