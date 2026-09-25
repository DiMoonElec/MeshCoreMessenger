using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Application;

public interface ICompanionSessionFactory
{
    Task<CompanionSession> CreateAsync(
        ConnectionProfile profile,
        long generation,
        CancellationToken cancellationToken = default);
}
