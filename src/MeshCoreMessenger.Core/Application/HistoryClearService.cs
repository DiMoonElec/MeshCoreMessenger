using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;

namespace MeshCoreMessenger.Core.Application;

public interface IHistoryClearService
{
    Task<string?> GetUnavailableReasonAsync(Guid nodeId, Guid conversationId, CancellationToken cancellationToken = default);
    Task<HistoryClearResult> ClearAsync(Guid nodeId, Guid conversationId, CancellationToken cancellationToken = default);
}

public sealed class HistoryClearService(IHistoryClearStore store, ConversationOperationGuard operations,
    ConversationReadStateTracker reads, IDurableOutgoingWrites outgoing) : IHistoryClearService
{
    public async Task<string?> GetUnavailableReasonAsync(Guid nodeId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        if (reads.IsPaused || outgoing.IsPaused) return "Сначала восстановите сохранение локальных данных.";
        var status = await store.GetStatusAsync(nodeId, conversationId, cancellationToken).ConfigureAwait(false);
        if (status.HasPendingSend) return "Отправка или ожидание подтверждения ещё не завершены.";
        if (operations.IsBusy(nodeId, status.Kind, status.Identity)) return "В переписке выполняется другая операция. Дождитесь завершения.";
        return status.MessageCount == 0 && !status.HasDeliveryHistory ? "Переписка уже пуста." : null;
    }
    public async Task<HistoryClearResult> ClearAsync(Guid nodeId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        if (outgoing.IsPaused) throw new InvalidOperationException("Сначала восстановите сохранение статусов отправки.");
        var status = await store.GetStatusAsync(nodeId, conversationId, cancellationToken).ConfigureAwait(false);
        using var admission = operations.BeginClear(nodeId, status.Kind, status.Identity);
        return await reads.ClearHistoryAsync(() => store.ClearAsync(nodeId, conversationId, cancellationToken), cancellationToken).ConfigureAwait(false);
    }
}
