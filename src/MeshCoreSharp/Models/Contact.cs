namespace MeshCoreSharp.Models;

/// <summary>A contact advertised by a MeshCore node.</summary>
/// <param name="OutPathLength">Encoded firmware path length; 0xFF means unknown.</param>
/// <param name="OutPath">Entire 64-byte wire field, including unused bytes.</param>
/// <param name="LastAdvertTimestamp">Unix seconds according to the remote node's clock.</param>
/// <param name="LastModified">Unix seconds according to the local Companion's clock.</param>
public sealed record Contact(
    ReadOnlyMemory<byte> PublicKey,
    AdvertisementType AdvertisementType,
    byte Flags,
    byte OutPathLength,
    ReadOnlyMemory<byte> OutPath,
    string Name,
    uint LastAdvertTimestamp,
    double AdvertisementLatitude,
    double AdvertisementLongitude,
    uint LastModified)
{
    public string PublicKeyHex => Convert.ToHexString(PublicKey.Span);
}
