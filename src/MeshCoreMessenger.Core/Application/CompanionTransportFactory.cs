using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Serial;
using MeshCoreSharp.Transport.Tcp;

namespace MeshCoreMessenger.Core.Application;

/// <summary>Portable TCP/Serial provider. Native BLE is supplied by the desktop host.</summary>
public sealed class CompanionTransportFactory : ICompanionTransportFactory
{
    public IMeshCoreTransport Create(ConnectionProfile profile)
    {
        var mapping = ConnectionProfileMapper.Map(profile);
        return mapping.Transport switch
        {
            ConnectionTransportKind.Tcp => new TcpMeshCoreTransport(mapping.TcpOptions!),
            ConnectionTransportKind.Serial => new SerialMeshCoreTransport(mapping.SerialOptions!),
            ConnectionTransportKind.Ble => throw new MeshCoreBleTransportException(
                MeshCoreBleFailureReason.UnsupportedPlatform, "BLE requires a Windows desktop transport provider."),
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
    }
}
