using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence;

public interface INodeStore
{
    Task<NodeRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<NodeRecord?> GetByPublicKeyAsync(
        ReadOnlyMemory<byte> publicKey,
        CancellationToken cancellationToken = default);
    Task<NodeRecord> FindOrCreateAsync(
        ReadOnlyMemory<byte> publicKey,
        string? name,
        DateTimeOffset seenUtc,
        CancellationToken cancellationToken = default);
}
