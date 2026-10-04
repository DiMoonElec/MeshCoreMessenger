using MeshCoreMessenger.Core.Domain;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class DirectoryServiceTests
{
    [Fact]
    public async Task RouteReadbackSurvivesOlderSnapshotWhileNewerLearnedRouteCanReplaceIt()
    {
        await using var f = await StorageContext.CreateAsync();
        var alice = Contact(1, "Alice") with { OutPathLength = 2 };
        var bob = Contact(2, "Bob") with { OutPathLength = 0 };
        await f.Storage.Directories.ApplySnapshotAsync(f.Node.Id, f.Session.Id, [alice, bob], [], f.Now, CancellationToken);
        var notifications = 0;
        Task<ContactDetailsProjection?>? afterCommit = null;
        f.Storage.Directories.ContactRouteCommitted += (_, commit) =>
        {
            notifications++;
            Assert.Equal(f.Node.Id, commit.NodeId);
            Assert.Equal(f.Session.Id, commit.SessionId);
            afterCommit = f.Storage.ConversationDirectory.GetContactDetailsAsync(commit.NodeId, commit.PublicKey, CancellationToken);
        };
        f.Storage.Directories.ContactRouteCommitted += (_, _) => throw new InvalidOperationException("Broken UI subscriber");
        await f.Storage.Directories.UpdateContactRouteAsync(f.Node.Id, f.Session.Id, alice.PublicKey, alice.OutPath, 0xFF, f.Now.AddMinutes(1), CancellationToken);
        Assert.Equal(byte.MaxValue, (await afterCommit!)!.OutPathLength);
        var stale = await f.Storage.Directories.ApplySnapshotAsync(f.Node.Id, f.Session.Id, [alice, bob], [], f.Now, CancellationToken);
        Assert.Equal(byte.MaxValue, stale.CurrentContacts.Single(contact => contact.PublicKey.SequenceEqual(alice.PublicKey)).OutPathLength);
        var current = await f.Storage.ConversationDirectory.GetContactDetailsAsync(f.Node.Id, alice.PublicKey, CancellationToken);
        Assert.Equal(byte.MaxValue, current!.OutPathLength);
        Assert.Equal(alice.OutPath, current.OutPath);
        Assert.Equal((byte)0, (await f.Storage.ConversationDirectory.GetContactDetailsAsync(f.Node.Id, bob.PublicKey, CancellationToken))!.OutPathLength);
        Assert.Equal(byte.MaxValue, Assert.Single(await f.Storage.Directories.GetCurrentContactsByPrefixAsync(f.Node.Id, alice.PublicKey[..6], CancellationToken)).OutPathLength);
        var learned = alice with { OutPathLength = 0x42 };
        await f.Storage.Directories.ApplySnapshotAsync(f.Node.Id, f.Session.Id, [learned, bob], [], f.Now.AddMinutes(2), CancellationToken);
        Assert.Equal((byte)0x42, (await f.Storage.ConversationDirectory.GetContactDetailsAsync(f.Node.Id, alice.PublicKey, CancellationToken))!.OutPathLength);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.Directories.UpdateContactRouteAsync(f.Node.Id, f.Session.Id,
            alice.PublicKey, alice.OutPath, 0xFF, f.Now.AddMinutes(1), CancellationToken));
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task RouteReadbackRejectsWrongSessionMissingContactAndPartialWireFields()
    {
        await using var f = await StorageContext.CreateAsync();
        var alice = Contact(1, "Alice");
        await f.Storage.Directories.ApplySnapshotAsync(f.Node.Id, f.Session.Id, [alice], [], f.Now, CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.Directories.UpdateContactRouteAsync(f.Node.Id, Guid.NewGuid(), alice.PublicKey, alice.OutPath, 0xFF, f.Now, CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.Directories.UpdateContactRouteAsync(f.Node.Id, f.Session.Id, Contact(2, "Missing").PublicKey, alice.OutPath, 0xFF, f.Now, CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Storage.Directories.UpdateContactRouteAsync(f.Node.Id, f.Session.Id, alice.PublicKey, alice.OutPath, 0xC1, f.Now, CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Storage.Directories.UpdateContactRouteAsync(f.Node.Id, f.Session.Id, alice.PublicKey[..6], alice.OutPath, 0xFF, f.Now, CancellationToken));
    }
}
