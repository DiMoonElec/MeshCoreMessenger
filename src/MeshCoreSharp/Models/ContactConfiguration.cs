namespace MeshCoreSharp.Models;

/// <summary>Complete application-facing data used to create or update a Companion contact.</summary>
/// <param name="OutPathLength">Encoded MeshCore path descriptor; 0xFF means an unknown path.</param>
/// <param name="OutPath">Entire 64-byte path field, including unused bytes.</param>
public sealed record ContactConfiguration(
    ReadOnlyMemory<byte> PublicKey,
    AdvertisementType AdvertisementType,
    byte Flags,
    byte OutPathLength,
    ReadOnlyMemory<byte> OutPath,
    string Name,
    uint LastAdvertTimestamp,
    double AdvertisementLatitude,
    double AdvertisementLongitude)
{
    /// <summary>Creates an independent configuration snapshot from a decoded contact.</summary>
    public static ContactConfiguration FromContact(Contact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        return new ContactConfiguration(
            contact.PublicKey.ToArray(),
            contact.AdvertisementType,
            contact.Flags,
            contact.OutPathLength,
            contact.OutPath.ToArray(),
            contact.Name,
            contact.LastAdvertTimestamp,
            contact.AdvertisementLatitude,
            contact.AdvertisementLongitude);
    }
}
