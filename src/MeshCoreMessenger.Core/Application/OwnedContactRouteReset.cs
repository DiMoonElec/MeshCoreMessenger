using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Application;

/// <summary>Readback after one owned reset; never reacquires a session or repeats a mutation.</summary>
internal static class OwnedContactRouteReset
{
    internal static async Task<Contact> ResetAsync(SessionCommandLease owner, IDirectoryStore store,
        TimeProvider timeProvider, CancellationToken token)
    {
        await owner.ResetPathAsync().ConfigureAwait(false);
        return await ReadAndPersistAsync(owner, store, timeProvider, token).ConfigureAwait(false);
    }

    internal static async Task<Contact> ReadAndPersistAsync(SessionCommandLease owner, IDirectoryStore store,
        TimeProvider timeProvider, CancellationToken token)
    {
        var contact = await owner.GetContactAsync().ConfigureAwait(false);
        if (owner.Target is not ContactCommandTarget target || !contact.PublicKey.Span.SequenceEqual(target.PublicKey.Span))
            throw new InvalidDataException("Contact readback returned a different key.");
        await store.UpdateContactRouteAsync(owner.Owner.NodeId, owner.Owner.SessionId, contact.PublicKey,
            contact.OutPath, contact.OutPathLength, timeProvider.GetUtcNow(), token).ConfigureAwait(false);
        return contact;
    }
}
