namespace MeshCoreSharp.Models;

/// <summary>A firmware notification about a received advertisement.</summary>
/// <param name="DiscoveredContact">Details supplied by NEW_ADVERT; null for the key-only ADVERT notification.</param>
public sealed record AdvertisementInfo(ReadOnlyMemory<byte> PublicKey, Contact? DiscoveredContact)
{
    public string PublicKeyHex => Convert.ToHexString(PublicKey.Span);
    /// <summary>The firmware reported a new discovery; this does not guarantee it was saved to the contact table.</summary>
    public bool IsNew => DiscoveredContact is not null;
}
