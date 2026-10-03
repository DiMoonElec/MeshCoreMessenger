using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Application;

public sealed record ContactRouteResetRequest(Guid NodeId, Guid SessionId, long Generation, ReadOnlyMemory<byte> PublicKey);
public sealed record ContactRouteResetResult(Guid NodeId, ReadOnlyMemory<byte> PublicKey, bool LocalUpdated, string? Warning);
public interface IContactRouteService
{
    Task<string?> GetUnavailableReasonAsync(ContactRouteResetRequest request, CancellationToken cancellationToken = default);
    Task<ContactRouteResetResult> ResetAsync(ContactRouteResetRequest request, CancellationToken cancellationToken = default);
}

/// <summary>One explicit contact mutation, owned by an immutable Online session. Never transmits a message or retries.</summary>
public sealed class ContactRouteService(ISessionCommandGateway gateway, IConversationDirectoryReader directory,
    IDirectoryStore store, ConversationOperationGuard operations, TimeProvider timeProvider) : IContactRouteService
{
    public async Task<string?> GetUnavailableReasonAsync(ContactRouteResetRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request.PublicKey.Length != 32) return "Нужен полный ключ личного контакта.";
            request = request with { PublicKey = request.PublicKey.ToArray() };
            if (operations.IsBusy(request.NodeId, ConversationKind.Contact, request.PublicKey)) return "Отправка, ожидание ACK или другая операция ещё не завершены.";
            await using var lease = await AcquireAsync(request, cancellationToken).ConfigureAwait(false);
            return await lease.RunAsync((_, token) => ValidateContactAsync(request, token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (InvalidOperationException) { return "Для сброса маршрута нужна активная сессия этой ноды."; }
    }

    public async Task<ContactRouteResetResult> ResetAsync(ContactRouteResetRequest request, CancellationToken cancellationToken = default)
    {
        request = request with { PublicKey = request.PublicKey.ToArray() };
        using var admission = operations.BeginExclusive(request.NodeId, ConversationKind.Contact, request.PublicKey);
        await using var lease = await AcquireAsync(request, cancellationToken).ConfigureAwait(false);
        return await lease.RunAsync(async (owned, token) =>
        {
            if (await ValidateContactAsync(request, token).ConfigureAwait(false) is { } reason) throw new InvalidOperationException(reason);
            await owned.ResetPathAsync().ConfigureAwait(false);
            try
            {
                var observedUtc = timeProvider.GetUtcNow();
                var contacts = await owned.GetContactsAsync().ConfigureAwait(false);
                var contact = contacts.SingleOrDefault(item => item.PublicKey.Span.SequenceEqual(request.PublicKey.Span))
                    ?? throw new InvalidOperationException("Contact no longer present in route readback.");
                await store.UpdateContactRouteAsync(owned.Owner.NodeId, owned.Owner.SessionId, contact.PublicKey,
                    contact.OutPath, contact.OutPathLength, observedUtc, token).ConfigureAwait(false);
                return new ContactRouteResetResult(request.NodeId, request.PublicKey, true, null);
            }
            catch (Exception)
            {
                // OK is already confirmed. A readback/local write failure must not repeat the mutation.
                return new ContactRouteResetResult(request.NodeId, request.PublicKey, false,
                    "Маршрут сброшен. Не удалось обновить локальные данные; подключитесь к ноде заново для обновления справочника.");
            }
        }).ConfigureAwait(false);
    }

    private async Task<SessionCommandLease> AcquireAsync(ContactRouteResetRequest request, CancellationToken token)
    {
        var lease = gateway.Acquire(request.NodeId, new ContactCommandTarget(request.PublicKey), token);
        if (lease.Owner.SessionId == request.SessionId && lease.Owner.Generation == request.Generation) return lease;
        await lease.DisposeAsync().ConfigureAwait(false);
        throw new InvalidOperationException("Сессия изменилась. Откройте меню заново.");
    }

    private async Task<string?> ValidateContactAsync(ContactRouteResetRequest request, CancellationToken token)
    {
        var contact = await directory.GetContactDetailsAsync(request.NodeId, request.PublicKey, token).ConfigureAwait(false);
        return contact is { PresentOnNode: true, ContactType: (int)AdvertisementType.Chat }
            ? null : "Личный контакт отсутствует в справочнике ноды.";
    }
}
