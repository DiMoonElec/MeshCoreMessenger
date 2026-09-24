namespace MeshCoreSharp.Models;

/// <summary>A private message or room-server post. The contact prefix contains six bytes, not a full public key.</summary>
/// <param name="SenderPrefix">Four-byte author prefix for signed room posts; empty for other messages.</param>
public sealed record ContactMessage(
    ReadOnlyMemory<byte> ContactPublicKeyPrefix,
    byte PathLength,
    MessageTextType TextType,
    DateTimeOffset Timestamp,
    string Text,
    ReadOnlyMemory<byte> SenderPrefix,
    double? SnrDb) : ReceivedMessage(PathLength, SnrDb)
{
    public string ContactPublicKeyPrefixHex => Convert.ToHexString(ContactPublicKeyPrefix.Span);
}
