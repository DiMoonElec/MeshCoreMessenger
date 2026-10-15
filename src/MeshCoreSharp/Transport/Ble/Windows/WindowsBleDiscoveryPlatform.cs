using System.Runtime.InteropServices;
using MeshCoreSharp.Exceptions;
using global::Windows.Devices.Bluetooth;
using global::Windows.Devices.Bluetooth.Advertisement;
using global::Windows.Devices.Enumeration;
using global::Windows.Devices.Radios;

namespace MeshCoreSharp.Transport.Ble.Windows;

internal sealed class WindowsBleDiscoveryPlatform : IBleDiscoveryPlatform
{
    public async Task CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (adapter is null || !adapter.IsLowEnergySupported)
                throw new MeshCoreBleTransportException(MeshCoreBleFailureReason.AdapterUnavailable,
                    "No Bluetooth LE adapter is available.");
            var radio = await adapter.GetRadioAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (radio is null)
                throw new MeshCoreBleTransportException(MeshCoreBleFailureReason.AccessDenied,
                    "Windows did not grant access to the Bluetooth radio.");
            if (radio.State is RadioState.Off or RadioState.Disabled)
                throw new MeshCoreBleTransportException(MeshCoreBleFailureReason.RadioOff,
                    "The Bluetooth radio is turned off or disabled.");
            if (radio.State != RadioState.On)
                throw new MeshCoreBleTransportException(MeshCoreBleFailureReason.AdapterUnavailable,
                    "The Bluetooth radio is not ready.");
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException)
        {
            throw NativeError(error);
        }
    }

    public IBleAdvertisementScanner CreateScanner() => new Scanner();

    public async Task<IReadOnlyList<BleDeviceInfo>> GetPairedDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(
                BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), [],
                DeviceInformationKind.AssociationEndpoint).AsTask(cancellationToken).ConfigureAwait(false);
            return devices.Select(ToInfo).ToArray();
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException)
        {
            throw NativeError(error);
        }
    }

    public async Task<BleDeviceInfo?> ResolveAsync(BleAddress address, CancellationToken cancellationToken)
    {
        try
        {
            // Use the OS selector with the address type; never fabricate a Windows device ID,
            // match by display name, or call FromBluetoothAddressAsync (which creates a device object).
            var addressType = address.Kind switch
            {
                BleAddressKind.Public => BluetoothAddressType.Public,
                BleAddressKind.Random => BluetoothAddressType.Random,
                _ => BluetoothAddressType.Unspecified,
            };
            var selector = BluetoothLEDevice.GetDeviceSelectorFromBluetoothAddress(address.Value, addressType);
            return await FindEndpointAsync(selector, address, addressType, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException)
        {
            throw NativeError(error);
        }
    }

    private static async Task<BleDeviceInfo?> FindEndpointAsync(string selector, BleAddress address,
        BluetoothAddressType addressType, CancellationToken cancellationToken)
    {
        const string addressProperty = "System.Devices.Aep.DeviceAddress";
        const string typeProperty = "System.Devices.Aep.Bluetooth.Le.AddressType";
        var watcher = DeviceInformation.CreateWatcher(selector, [addressProperty, typeProperty],
            DeviceInformationKind.AssociationEndpoint);
        var result = new TaskCompletionSource<BleDeviceInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new object();
        var accepting = true;
        var entries = new Dictionary<string, DeviceInformation>(StringComparer.Ordinal);

        bool Matches(DeviceInformation device)
        {
            if (!device.Properties.TryGetValue(addressProperty, out var rawAddress) || rawAddress is not string text)
                return false;
            text = text.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
            if (!ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) || value != address.Value)
                return false;
            if (address.Kind == BleAddressKind.Unspecified) return true;
            return device.Properties.TryGetValue(typeProperty, out var type) && type switch
            {
                byte valueType => valueType == (int)addressType,
                ushort valueType => valueType == (int)addressType,
                uint valueType => valueType == (uint)addressType,
                int valueType => valueType == (int)addressType,
                _ => false,
            };
        }
        void Consider(DeviceInformation device)
        {
            if (Matches(device)) result.TrySetResult(ToInfo(device));
        }
        void OnAdded(DeviceWatcher sender, DeviceInformation device)
        {
            lock (sync)
            {
                if (!accepting) return;
                entries[device.Id] = device;
                Consider(device);
            }
        }
        void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            lock (sync)
            {
                if (!accepting || !entries.TryGetValue(update.Id, out var device)) return;
                device.Update(update);
                Consider(device);
            }
        }
        void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            lock (sync) { if (accepting) entries.Remove(update.Id); }
        }
        void OnCompleted(DeviceWatcher sender, object args) => result.TrySetResult(null);
        void OnStopped(DeviceWatcher sender, object args)
        {
            lock (sync)
                if (accepting) result.TrySetException(new MeshCoreBleTransportException(
                    MeshCoreBleFailureReason.TransportFailure, "Windows endpoint enumeration stopped unexpectedly."));
        }
        watcher.Added += OnAdded;
        watcher.Updated += OnUpdated;
        watcher.Removed += OnRemoved;
        watcher.EnumerationCompleted += OnCompleted;
        watcher.Stopped += OnStopped;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            watcher.Start();
            return await result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (sync) accepting = false;
            watcher.Added -= OnAdded;
            watcher.Updated -= OnUpdated;
            watcher.Removed -= OnRemoved;
            watcher.EnumerationCompleted -= OnCompleted;
            watcher.Stopped -= OnStopped;
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                watcher.Stop();
        }
    }

    private static BleDeviceInfo ToInfo(DeviceInformation device) => new()
    {
        DeviceId = device.Id,
        Name = device.Name,
        IsPaired = device.Pairing.IsPaired,
    };

    private static MeshCoreBleTransportException NativeError(Exception error) => new(
        error.HResult == unchecked((int)0x80070005)
            ? MeshCoreBleFailureReason.AccessDenied : MeshCoreBleFailureReason.TransportFailure,
        "Windows Bluetooth discovery failed.", error, nativeHResult: error.HResult);

    private sealed class Scanner : IBleAdvertisementScanner
    {
        private readonly BluetoothLEAdvertisementWatcher _watcher = new()
        {
            // Active scanning receives scan responses, which may carry the name or NUS UUID separately.
            ScanningMode = BluetoothLEScanningMode.Active,
        };
        private bool _disposed;
        public event Action<BleAdvertisement>? Received;
        public event Action<Exception?>? Stopped;

        internal Scanner()
        {
            _watcher.Received += OnReceived;
            _watcher.Stopped += OnStopped;
        }

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try { _watcher.Start(); }
            catch (Exception error) when (error is COMException or UnauthorizedAccessException)
            {
                throw NativeError(error);
            }
        }

        public void Stop()
        {
            if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                _watcher.Stop();
        }

        private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var kind = args.BluetoothAddressType switch
            {
                BluetoothAddressType.Public => BleAddressKind.Public,
                BluetoothAddressType.Random => BleAddressKind.Random,
                _ => BleAddressKind.Unspecified,
            };
            Received?.Invoke(new(new(args.BluetoothAddress, kind), args.Advertisement.LocalName,
                args.Advertisement.ServiceUuids.Contains(BleGattProfile.Service)));
        }

        private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            if (args.Error == BluetoothError.Success)
            {
                Stopped?.Invoke(null);
                return;
            }
            var reason = args.Error switch
            {
                BluetoothError.RadioNotAvailable => MeshCoreBleFailureReason.RadioOff,
                BluetoothError.ConsentRequired => MeshCoreBleFailureReason.AccessDenied,
                _ => MeshCoreBleFailureReason.TransportFailure,
            };
            Stopped?.Invoke(new MeshCoreBleTransportException(reason,
                "Windows stopped the BLE advertisement watcher.", nativeStatus: (int)args.Error));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Stop(); }
            finally
            {
                _watcher.Received -= OnReceived;
                _watcher.Stopped -= OnStopped;
                Received = null;
                Stopped = null;
            }
        }
    }
}
