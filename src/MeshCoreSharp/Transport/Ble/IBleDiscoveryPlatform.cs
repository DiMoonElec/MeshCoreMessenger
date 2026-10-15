namespace MeshCoreSharp.Transport.Ble;

internal enum BleAddressKind { Unspecified, Public, Random }
internal readonly record struct BleAddress(ulong Value, BleAddressKind Kind);
internal sealed record BleAdvertisement(BleAddress Address, string? Name, bool HasCompanionService);

// Only discovery primitives: implementations must not open BluetoothLEDevice or perform GATT access.
internal interface IBleDiscoveryPlatform
{
    Task CheckAvailabilityAsync(CancellationToken cancellationToken);
    IBleAdvertisementScanner CreateScanner();
    Task<IReadOnlyList<BleDeviceInfo>> GetPairedDevicesAsync(CancellationToken cancellationToken);
    Task<BleDeviceInfo?> ResolveAsync(BleAddress address, CancellationToken cancellationToken);
}

internal interface IBleAdvertisementScanner : IDisposable
{
    event Action<BleAdvertisement>? Received;
    event Action<Exception?>? Stopped;
    void Start();
    void Stop();
}
