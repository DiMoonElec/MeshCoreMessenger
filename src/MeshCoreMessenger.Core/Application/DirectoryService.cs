using System.Security.Cryptography;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Application;

/// <summary>Reads complete Companion directories and persists only a validated, secret-free snapshot.</summary>
public sealed class DirectoryService(IDirectoryStore directories, TimeProvider timeProvider)
{
    public async Task<DirectorySnapshotResult> SynchronizeAsync(
        CompanionSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var nodeId = session.LocalNodeId
            ?? throw new InvalidOperationException("Companion session has not identified its local node.");

        // Do not persist contacts until channel enumeration has also completed. An exception from
        // either command leaves the previous snapshot untouched.
        var contacts = await session.GetContactsAsync(cancellationToken).ConfigureAwait(false);
        var channels = await session.GetChannelsAsync(cancellationToken).ConfigureAwait(false);
        var contactSnapshot = contacts.Select(ToSnapshot).ToArray();
        var channelSnapshot = channels
            .Where(channel => !channel.IsEmpty)
            .Select(ToSnapshot)
            .ToArray();
        return await directories.ApplySnapshotAsync(
            nodeId,
            session.SessionId,
            contactSnapshot,
            channelSnapshot,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
    }

    public Task CommitPendingChannelTransitionsAsync(
        DirectorySnapshotResult snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return directories.CommitPendingChannelTransitionsAsync(
            snapshot.PendingChannelTransitions,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private static DirectoryContactSnapshot ToSnapshot(Contact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        if (contact.PublicKey.Length != 32 || contact.OutPath.Length != 64)
        {
            throw new InvalidDataException("Companion returned an incomplete contact record.");
        }

        return new DirectoryContactSnapshot(
            contact.PublicKey.ToArray(),
            contact.Name,
            (int)contact.AdvertisementType,
            contact.Flags,
            contact.OutPath.ToArray(),
            DateTimeOffset.FromUnixTimeSeconds(contact.LastAdvertTimestamp),
            contact.AdvertisementLatitude,
            contact.AdvertisementLongitude);
    }

    private static DirectoryChannelSnapshot ToSnapshot(ChannelInfo channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.Secret.Length != 16)
        {
            throw new InvalidDataException("Companion returned a channel secret that is not 16 bytes long.");
        }

        var secret = channel.Secret.ToArray();
        var isVerifiedHashtag = channel.Name.StartsWith('#') &&
            ChannelSecrets.DeriveHashtag(channel.Name).AsSpan().SequenceEqual(secret);
        return new DirectoryChannelSnapshot(
            channel.Index,
            channel.Name,
            SHA256.HashData(secret),
            isVerifiedHashtag ? ChannelAccessKind.PublicOrHashtag : ChannelAccessKind.Unknown);
    }
}
