using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence;

public interface IIncomingMessageStore
{
    Task<StoredIncomingMessage> StoreAsync(
        IncomingMessageEnvelope envelope,
        CancellationToken cancellationToken = default);
}
