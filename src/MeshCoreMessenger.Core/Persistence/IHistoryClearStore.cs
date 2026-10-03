using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence;

public interface IHistoryClearStore
{
    Task<HistoryClearStatus> GetStatusAsync(Guid nodeId, Guid conversationId, CancellationToken cancellationToken = default);
    Task<HistoryClearResult> ClearAsync(Guid nodeId, Guid conversationId, CancellationToken cancellationToken = default);
}
