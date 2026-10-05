using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence;

public interface IContactDeliveryHistoryReader
{
    /// <summary>Newest-first successes by full node/contact identity, without message text or secrets.</summary>
    Task<ContactDeliveryPage> GetPageAsync(Guid nodeId, ReadOnlyMemory<byte> contactPublicKey, int limit = 50,
        ContactDeliveryCursor? before = null, CancellationToken cancellationToken = default);
}
