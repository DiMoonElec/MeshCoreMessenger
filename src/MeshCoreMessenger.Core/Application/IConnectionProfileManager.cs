using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Application;

public interface IConnectionProfileManager
{
    Task<IReadOnlyList<ConnectionProfile>> GetProfilesAsync(CancellationToken cancellationToken = default);
    Task<ConnectionProfile?> GetSelectedProfileAsync(CancellationToken cancellationToken = default);
    Task<ConnectionProfile> SaveAndSelectAsync(
        ConnectionProfileDraft draft,
        CancellationToken cancellationToken = default);
    Task<ConnectionProfile> UpdateExpectedNodePublicKeyAsync(
        Guid profileId,
        ReadOnlyMemory<byte> publicKey,
        CancellationToken cancellationToken = default);
}
