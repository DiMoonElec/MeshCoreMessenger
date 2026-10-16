using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Ble;

namespace MeshCoreMessenger.Desktop.Platform;

internal sealed class DesktopCompanionTransportFactory : ICompanionTransportFactory
{
    private readonly CompanionTransportFactory _portable = new();
    private IBleDeviceAccessDispatcher Dispatcher { get; }

    public DesktopCompanionTransportFactory(IBleDeviceAccessDispatcher dispatcher) =>
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public IMeshCoreTransport Create(ConnectionProfile profile)
    {
#if WINDOWS
        if (profile.Transport == ConnectionTransportKind.Ble)
            return new BleMeshCoreTransport(new()
            {
                DeviceId = profile.BleDeviceId!, DeviceAccessDispatcher = Dispatcher,
            });
#endif
        return _portable.Create(profile);
    }
}
