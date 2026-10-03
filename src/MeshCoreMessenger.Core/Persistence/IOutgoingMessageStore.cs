using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence;

public interface IOutgoingMessageStore
{
    event EventHandler<OutgoingMessageCommit>? MessageCommitted;
    Task<PreparedOutgoingMessage> PrepareAsync(PrepareOutgoingMessage message, CancellationToken cancellationToken = default);
    Task<bool> TransitionAsync(OutgoingAttemptTransition transition, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OutgoingAttemptSnapshot>> GetAttemptsAsync(Guid nodeId, Guid messageId, CancellationToken cancellationToken = default);
}
