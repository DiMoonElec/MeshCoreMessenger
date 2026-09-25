using MeshCoreSharp;

namespace MeshCoreMessenger.Core.Application;

public interface IMeshCoreClientFactory
{
    MeshCoreClient Create(Domain.ConnectionProfile profile);
}
