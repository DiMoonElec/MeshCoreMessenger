using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Transport;

namespace MeshCoreMessenger.Core.Application;

/// <summary>Creates one fresh, disconnected transport per connection attempt.</summary>
public interface ICompanionTransportFactory
{
    IMeshCoreTransport Create(ConnectionProfile profile);
}
