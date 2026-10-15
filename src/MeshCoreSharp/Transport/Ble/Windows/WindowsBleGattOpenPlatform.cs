using System.Runtime.InteropServices;
using MeshCoreSharp.Exceptions;
using global::Windows.Devices.Bluetooth;
using global::Windows.Devices.Bluetooth.GenericAttributeProfile;
using global::Windows.Devices.Enumeration;

namespace MeshCoreSharp.Transport.Ble.Windows;

internal sealed class WindowsBleGattOpenPlatform(IBleDeviceAccessDispatcher dispatcher) : IBleGattOpenPlatform
{
    private readonly IBleDeviceAccessDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public async Task<IBleGattConnection?> OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        WindowsBleGattConnection? connection = null;
        BluetoothLEDevice? device = null;
        try
        {
            device = await BleDeviceAccess.OpenAsync(_dispatcher,
                () => BluetoothLEDevice.FromIdAsync(deviceId).AsTask(), cancellationToken).ConfigureAwait(false);
            if (device is null) return null;
            connection = new WindowsBleGattConnection(device);
            device = null; // Connection now owns the native device, including on discovery failure.
            await connection.InitializeAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return connection;
        }
        catch (Exception error)
        {
            try
            {
                if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
                device?.Dispose();
            }
            catch (Exception cleanupError) { error.Data["BleCleanupError"] = cleanupError; }
            if (error is COMException or UnauthorizedAccessException)
                throw WindowsBleGattErrors.FromNative(error);
            throw;
        }
    }
}

internal static class WindowsBleGattErrors
{
    internal static void ThrowIfFailed(GattCommunicationStatus status, byte? protocolError,
        bool isPaired, DeviceAccessStatus accessStatus)
    {
        if (status == GattCommunicationStatus.Success) return;
        var reason = status switch
        {
            GattCommunicationStatus.Unreachable => MeshCoreBleFailureReason.DeviceUnreachable,
            GattCommunicationStatus.AccessDenied => !isPaired && accessStatus == DeviceAccessStatus.Allowed
                ? MeshCoreBleFailureReason.PairingRequired : MeshCoreBleFailureReason.AccessDenied,
            GattCommunicationStatus.ProtocolError when RequiresAuthentication(protocolError) => isPaired
                    ? MeshCoreBleFailureReason.AccessDenied : MeshCoreBleFailureReason.PairingRequired,
            _ => MeshCoreBleFailureReason.TransportFailure,
        };
        throw new MeshCoreBleTransportException(reason, reason == MeshCoreBleFailureReason.PairingRequired
            ? "Pair the selected BLE device in Windows Bluetooth settings before retrying."
            : "Windows GATT discovery failed.", nativeStatus: (int)status, protocolError: protocolError);
    }

    private static bool RequiresAuthentication(byte? error) =>
        error == GattProtocolError.InsufficientAuthentication || error == GattProtocolError.InsufficientAuthorization
        || error == GattProtocolError.InsufficientEncryption || error == GattProtocolError.InsufficientEncryptionKeySize;

    internal static MeshCoreBleTransportException FromNative(Exception error) => new(
        error is UnauthorizedAccessException || error.HResult == unchecked((int)0x80070005)
            ? MeshCoreBleFailureReason.AccessDenied : MeshCoreBleFailureReason.TransportFailure,
        "Windows BLE device access failed.", error, nativeHResult: error.HResult);
}

// Preparation only. Notification subscription/write operations are added in BLE5/BLE6, not stubbed here.
internal sealed class WindowsBleGattConnection : IBleGattConnection
{
    private readonly BluetoothLEDevice _device;
    private readonly List<GattDeviceService> _services = [];
    private GattSession? _session;
    private int _disposed;
    private int _linkConnected;
    private int _sawConnected;
    private int _disconnected;
    private int _maxPduSize;

    internal WindowsBleGattConnection(BluetoothLEDevice device)
    {
        _device = device;
        IsPaired = device.DeviceInformation.Pairing.IsPaired;
    }

    internal bool IsPaired { get; }
    internal GattCharacteristic? ReceiveFromApp { get; private set; }
    internal GattCharacteristic? SendToApp { get; private set; }
    public BleGattCapabilities Capabilities { get; private set; } = new(false, false, false, false, false);
    public ushort MaxPduSize => (ushort)Volatile.Read(ref _maxPduSize);
    public bool IsLinkConnected => Volatile.Read(ref _disposed) == 0
        && Volatile.Read(ref _disconnected) == 0 && Volatile.Read(ref _linkConnected) != 0;
    public event Action<Exception?>? Disconnected;
    public event Action<ushort>? MaxPduSizeChanged;

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
        RefreshLink();
        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask().ConfigureAwait(false);
        if (_session is null)
            throw new MeshCoreBleTransportException(MeshCoreBleFailureReason.DeviceUnreachable,
                "Windows could not create a GATT session for the selected endpoint.");
        _session.MaxPduSizeChanged += OnMaxPduSizeChanged;
        _session.SessionStatusChanged += OnSessionStatusChanged;
        Volatile.Write(ref _maxPduSize, _session.MaxPduSize);
        // Do not set MaintainConnection: the application owns reconnect policy.
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _device.GetGattServicesForUuidAsync(BleGattProfile.Service, BluetoothCacheMode.Uncached)
            .AsTask().ConfigureAwait(false);
        _services.AddRange(result.Services); // Own every returned IClosable before inspecting status/cancellation.
        cancellationToken.ThrowIfCancellationRequested();
        CheckStatus(result.Status, result.ProtocolError);
        if (_services.Count == 0) return;
        if (_services.Count != 1)
            throw new MeshCoreBleTransportException(MeshCoreBleFailureReason.TransportFailure,
                "The endpoint exposes more than one NUS service.");

        var characteristics = await _services[0].GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
            .AsTask().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        CheckStatus(characteristics.Status, characteristics.ProtocolError);
        var rx = characteristics.Characteristics.Where(item => item.Uuid == BleGattProfile.AppToCompanion).ToArray();
        var tx = characteristics.Characteristics.Where(item => item.Uuid == BleGattProfile.CompanionToApp).ToArray();
        if (rx.Length > 1 || tx.Length > 1)
            throw new MeshCoreBleTransportException(MeshCoreBleFailureReason.TransportFailure,
                "The endpoint exposes ambiguous NUS characteristics.");
        ReceiveFromApp = rx.SingleOrDefault();
        SendToApp = tx.SingleOrDefault();
        Capabilities = new(true, ReceiveFromApp is not null, SendToApp is not null,
            ReceiveFromApp?.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write) == true,
            SendToApp?.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) == true);
        RefreshLink();
        Volatile.Write(ref _maxPduSize, _session.MaxPduSize);
    }

    private void CheckStatus(GattCommunicationStatus status, byte? protocolError) =>
        WindowsBleGattErrors.ThrowIfFailed(status, protocolError, IsPaired, _device.DeviceAccessInformation.CurrentStatus);

    private void RefreshLink()
    {
        var connected = _device.ConnectionStatus == BluetoothConnectionStatus.Connected;
        Volatile.Write(ref _linkConnected, connected ? 1 : 0);
        if (connected) Volatile.Write(ref _sawConnected, 1);
        else if (Volatile.Read(ref _sawConnected) != 0) SignalDisconnect(null);
    }

    private void SignalDisconnect(Exception? error)
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _disconnected, 1) != 0) return;
        Disconnected?.Invoke(error ?? new MeshCoreBleTransportException(MeshCoreBleFailureReason.DeviceUnreachable,
            "The BLE link was disconnected."));
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { RefreshLink(); }
        catch (Exception error) when (error is ObjectDisposedException or COMException)
        {
            SignalDisconnect(WindowsBleGattErrors.FromNative(error));
        }
    }

    private void OnSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
    {
        if (args.Status == GattSessionStatus.Closed && Volatile.Read(ref _sawConnected) != 0)
            SignalDisconnect(new MeshCoreBleTransportException(MeshCoreBleFailureReason.DeviceUnreachable,
                "The Windows GATT session closed.", nativeStatus: (int)args.Error));
    }

    private void OnMaxPduSizeChanged(GattSession sender, object args)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            var size = sender.MaxPduSize;
            Volatile.Write(ref _maxPduSize, size);
            MaxPduSizeChanged?.Invoke(size);
        }
        catch (Exception error) when (error is ObjectDisposedException or COMException)
        {
            SignalDisconnect(WindowsBleGattErrors.FromNative(error));
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        var errors = new List<Exception>();
        void Release(Action action)
        {
            try { action(); }
            catch (Exception error) { errors.Add(error); }
        }
        Release(() => _device.ConnectionStatusChanged -= OnConnectionStatusChanged);
        if (_session is not null)
        {
            Release(() => _session.MaxPduSizeChanged -= OnMaxPduSizeChanged);
            Release(() => _session.SessionStatusChanged -= OnSessionStatusChanged);
        }
        // InitializeAsync is fully finished before disposal, even when its caller stopped waiting.
        foreach (var service in _services) Release(service.Dispose);
        _services.Clear();
        if (_session is not null) Release(_session.Dispose);
        Release(_device.Dispose);
        Disconnected = null;
        MaxPduSizeChanged = null;
        ReceiveFromApp = null;
        SendToApp = null;
        if (errors.Count != 0) return ValueTask.FromException(new AggregateException("BLE native cleanup failed.", errors));
        return ValueTask.CompletedTask;
    }
}
