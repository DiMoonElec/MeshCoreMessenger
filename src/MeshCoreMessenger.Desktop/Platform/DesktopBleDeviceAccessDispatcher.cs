using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreSharp.Transport.Ble;

namespace MeshCoreMessenger.Desktop.Platform;

// Registered with BLE transport composition in BLE12; no device is opened by constructing this adapter.
internal sealed class DesktopBleDeviceAccessDispatcher(IUiDispatcher dispatcher) : IBleDeviceAccessDispatcher
{
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) =>
        dispatcher.InvokeAsync(action, cancellationToken);
}
