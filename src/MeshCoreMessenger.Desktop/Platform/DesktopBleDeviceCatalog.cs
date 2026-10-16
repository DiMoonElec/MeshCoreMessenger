using System.Diagnostics;
using MeshCoreSharp.Transport.Ble;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop.Platform;

public interface IBleDeviceCatalog
{
    bool IsSupported { get; }
    Task<IReadOnlyList<BleDeviceChoice>> FindAsync(CancellationToken cancellationToken = default);
}

public interface IBluetoothSettingsLauncher
{
    void Open();
}

internal sealed class DesktopBleDeviceCatalog : IBleDeviceCatalog
{
    public bool IsSupported =>
#if WINDOWS
        OperatingSystem.IsWindows();
#else
        false;
#endif

    public async Task<IReadOnlyList<BleDeviceChoice>> FindAsync(CancellationToken cancellationToken = default)
    {
        var devices = await new BleDeviceDiscovery().FindAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return devices.Select(device => new BleDeviceChoice(device.DeviceId, device.Name,
            device.IsPaired, device.AdvertisesCompanionService, IsFound: true)).ToArray();
    }
}

internal sealed class BluetoothSettingsLauncher : IBluetoothSettingsLauncher
{
    public void Open()
    {
#if WINDOWS
        using var process = Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
#else
        throw new PlatformNotSupportedException("Bluetooth settings are available only on Windows.");
#endif
    }
}
