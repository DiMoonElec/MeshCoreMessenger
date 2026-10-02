using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Application;

public interface IConnectionProfileManager
{
    // Default preserves compatibility with existing manager implementations.
    Task<ConnectionProfile> SaveAsync(ConnectionProfileDraft draft, CancellationToken cancellationToken = default) =>
        Task.FromException<ConnectionProfile>(new NotSupportedException("This manager does not support saving without selection."));
    Task<IReadOnlyList<ConnectionProfile>> GetProfilesAsync(CancellationToken cancellationToken = default);
    Task<ConnectionProfile?> GetSelectedProfileAsync(CancellationToken cancellationToken = default);
    Task<ConnectionProfile> SelectAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);
    Task<ConnectionProfile> SaveAndSelectAsync(
        ConnectionProfileDraft draft,
        CancellationToken cancellationToken = default);
}
