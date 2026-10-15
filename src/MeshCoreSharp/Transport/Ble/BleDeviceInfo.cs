namespace MeshCoreSharp.Transport.Ble;

/// <summary>A discovery candidate, not a verified Companion or a persistent node identity.</summary>
public sealed record BleDeviceInfo
{
    public required string DeviceId { get; init; }
    public string? Name { get; init; }
    public bool IsPaired { get; init; }
    public bool AdvertisesCompanionService { get; init; }
}
