using System.Security.Cryptography;
using System.Text;
using MeshCoreSharp.Protocol;

namespace MeshCoreSharp;

/// <summary>Creates channel secrets using conventions shared by MeshCore applications.</summary>
public static class ChannelSecrets
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Derives the public, guessable secret used by a hashtag channel. The exact name,
    /// including the leading '#', is encoded as UTF-8 and hashed with SHA-256; the first
    /// 16 bytes form the channel secret.
    /// </summary>
    /// <remarks>Hashtag channel traffic must not be treated as private.</remarks>
    public static byte[] DeriveHashtag(string channelName)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelName);
        if (channelName[0] != '#' || channelName.Length == 1)
            throw new ArgumentException("A hashtag channel name must start with '#' and contain a name.", nameof(channelName));
        if (channelName.Contains('\0'))
            throw new ArgumentException("Channel name must not contain NUL.", nameof(channelName));

        var nameBytes = StrictUtf8.GetBytes(channelName);
        if (nameBytes.Length > ProtocolLimits.MaxChannelNameUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(channelName),
                $"Channel name exceeds the {ProtocolLimits.MaxChannelNameUtf8Bytes}-byte UTF-8 storage limit.");

        return SHA256.HashData(nameBytes)[..ProtocolLimits.ChannelSecretSize];
    }
}
