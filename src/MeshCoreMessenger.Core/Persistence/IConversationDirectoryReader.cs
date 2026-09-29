using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence;

/// <summary>Reads bounded, node-scoped and secret-free navigation projections.</summary>
public interface IConversationDirectoryReader
{
    Task<ConversationDirectoryPage> GetPageAsync(
        Guid nodeId,
        ConversationDirectorySection section,
        ConversationDirectoryCursor? after,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ContactDetailsProjection?> GetContactDetailsAsync(
        Guid nodeId,
        ReadOnlyMemory<byte> publicKey,
        CancellationToken cancellationToken = default);

    Task<ChannelDetailsProjection?> GetChannelDetailsAsync(
        Guid nodeId,
        ReadOnlyMemory<byte> keyFingerprint,
        CancellationToken cancellationToken = default);
}
