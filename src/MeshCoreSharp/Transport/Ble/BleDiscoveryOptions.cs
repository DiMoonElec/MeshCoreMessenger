namespace MeshCoreSharp.Transport.Ble;

/// <summary>Bounds a BLE scan and its metadata lookups; no device is connected or paired.</summary>
public sealed record BleDiscoveryOptions
{
    public TimeSpan ScanDuration { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Shared budget for adapter checks, and separately for all final metadata lookups.</summary>
    public TimeSpan LookupTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public bool IncludePairedDevices { get; init; } = true;
    /// <summary>Previously saved endpoints remain visible even when not advertising.</summary>
    public IReadOnlyList<BleDeviceInfo> KnownDevices { get; init; } = [];

    internal void Validate()
    {
        ValidateDuration(ScanDuration, nameof(ScanDuration));
        ValidateDuration(LookupTimeout, nameof(LookupTimeout));
        ArgumentNullException.ThrowIfNull(KnownDevices);
        foreach (var device in KnownDevices)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentException.ThrowIfNullOrWhiteSpace(device.DeviceId);
        }
    }

    private static void ValidateDuration(TimeSpan duration, string name)
    {
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(name);
    }
}
