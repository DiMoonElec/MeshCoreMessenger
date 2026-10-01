using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence;

/// <summary>Reads and writes exact local draft text through a stable node identity.</summary>
public interface IDraftStore
{
    Task<DraftRecord?> GetAsync(
        DraftTarget target,
        CancellationToken cancellationToken = default);

    Task<DraftRecord?> SaveAsync(
        DraftTarget target,
        string text,
        DateTimeOffset updatedUtc,
        CancellationToken cancellationToken = default);
}
