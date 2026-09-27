using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence;

public interface IDirectoryStore
{
    Task<DirectorySnapshotResult> ApplySnapshotAsync(
        Guid nodeId,
        Guid sessionId,
        IReadOnlyList<DirectoryContactSnapshot> contacts,
        IReadOnlyList<DirectoryChannelSnapshot> channels,
        DateTimeOffset observedUtc,
        CancellationToken cancellationToken = default);

    Task CommitPendingChannelTransitionsAsync(
        IReadOnlyList<PendingChannelTransition> transitions,
        DateTimeOffset observedUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContactRecord>> GetCurrentContactsByPrefixAsync(
        Guid nodeId,
        ReadOnlyMemory<byte> publicKeyPrefix,
        CancellationToken cancellationToken = default);

    Task<ChannelBindingRecord?> GetActiveChannelBindingAsync(
        Guid nodeId,
        byte slot,
        CancellationToken cancellationToken = default);
}
