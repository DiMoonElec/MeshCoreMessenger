using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence;

public interface ISessionStore
{
    Task<SessionRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task CreateAsync(SessionRecord session, CancellationToken cancellationToken = default);
    Task BindNodeAsync(Guid sessionId, Guid nodeId, CancellationToken cancellationToken = default);
    Task EndAsync(
        Guid sessionId,
        DateTimeOffset endedUtc,
        string reason,
        CancellationToken cancellationToken = default);
}
