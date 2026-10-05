using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class IncomingMessageStoreTests
{
    private static Task SeedContact(MessageStorageContext c, byte[] key) => c.Storage.Directories.ApplySnapshotAsync(
        c.Node.Id, c.Session.Id, [new(key, "Peer", 1, 0, new byte[64], c.Now, 0, 0)], [], c.Now, CancellationToken);

    [Fact]
    public async Task UpgradeV5BackfillsEventsWithoutRewritingHistoryAndMakesBackup()
    {
        await using var c = await MessageStorageContext.CreateAsync(); var key = Key(10); await SeedContact(c, key);
        var original = c.Envelope(Contact(key[..6], "legacy"));
        var first = await c.Storage.IncomingMessages.StoreAsync(original, CancellationToken);
        await c.Storage.DisposeAsync();
        using (var connection = new SqliteConnection($"Data Source={c.DatabasePath};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE IncomingMessageEvents; DROP INDEX IX_Messages_PrivateRetry; DELETE FROM SchemaMigrations WHERE Version=6; PRAGMA user_version=5;";
            command.ExecuteNonQuery();
        }
        await c.ReopenAsync();
        var replay = await c.Storage.IncomingMessages.StoreAsync(original, CancellationToken);
        var retry = await c.Storage.IncomingMessages.StoreAsync(original with { EventId = Guid.NewGuid() }, CancellationToken);
        Assert.False(replay.Inserted); Assert.False(retry.Inserted); Assert.Equal(first.MessageId, retry.MessageId);
        using var current = OpenReadOnly(c.DatabasePath);
        Assert.Equal(6, ScalarInt(current, "PRAGMA user_version;"));
        Assert.Equal(1, ScalarInt(current, "SELECT COUNT(*) FROM Messages;"));
        Assert.Equal(2, ScalarInt(current, "SELECT COUNT(*) FROM IncomingMessageEvents;"));
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(c.Root, "backups"), "*.db"));
        using var saved = OpenReadOnly(backup); Assert.Equal(5, ScalarInt(saved, "PRAGMA user_version;"));
        Assert.Equal(1, ScalarInt(saved, "SELECT COUNT(*) FROM Messages;"));
    }

    [Fact]
    public async Task ChannelRepeatsRemainSeparateAndConcurrentPrivateRetriesMerge()
    {
        await using var c = await MessageStorageContext.CreateAsync(); var key = Key(10); await SeedContact(c, key);
        var envelopes = Enumerable.Range(0, 8).Select(_ => c.Envelope(Contact(key[..6], "same"))).ToArray();
        var messages = await Task.WhenAll(envelopes.Select(e => c.Storage.IncomingMessages.StoreAsync(e, CancellationToken)));
        Assert.Single(messages, m => m.Inserted); Assert.Single(messages.Select(m => m.MessageId).Distinct());
        var snapshot = await c.Storage.Directories.ApplySnapshotAsync(c.Node.Id, c.Session.Id,
            [new(key, "Peer", 1, 0, new byte[64], c.Now, 0, 0)],
            [new(0, "Public", Fingerprint(20), ChannelAccessKind.PublicOrHashtag)], c.Now, CancellationToken);
        var binding = Assert.Single(snapshot.ActiveBindings);
        var channel = new ChannelMessage(0, 1, MessageTextType.Plain, c.Now, "same", null);
        var first = await c.Storage.IncomingMessages.StoreAsync(c.Envelope(channel, binding: binding), CancellationToken);
        var second = await c.Storage.IncomingMessages.StoreAsync(c.Envelope(channel, binding: binding), CancellationToken);
        Assert.True(first.Inserted); Assert.True(second.Inserted); Assert.NotEqual(first.MessageId, second.MessageId);
    }

    [Fact]
    public async Task PrivateRetryAcrossSessionsKeepsOneMessageSequenceUnreadAndFirstReception()
    {
        await using var c = await MessageStorageContext.CreateAsync(); var key = Key(10); await SeedContact(c, key);
        var original = c.Envelope(Contact(key[..6], "Привет 👋"));
        var first = await c.Storage.IncomingMessages.StoreAsync(original, CancellationToken);
        var newSession = c.Session with { Id = Guid.NewGuid() };
        await c.Storage.Sessions.CreateAsync(newSession, CancellationToken);
        var retry = original with { EventId = Guid.NewGuid(), SessionId = newSession.Id, ReceivedUtc = c.Now.AddDays(-1),
            Message = ((ContactMessage)original.Message) with { PathLength = 3, SnrDb = 9.5 } };
        var repeated = await c.Storage.IncomingMessages.StoreAsync(retry, CancellationToken);
        Assert.False(repeated.Inserted); Assert.Equal(first.MessageId, repeated.MessageId);
        Assert.Equal(first.LocalSequence, repeated.LocalSequence); Assert.Equal(retry.EventId, repeated.EventId);
        Assert.Equal(1, (await c.Storage.ReadStates.GetAsync(c.Node.Id, first.ConversationId, CancellationToken)).UnreadCount);
        var history = await c.Storage.History.GetMessagesAsync(c.Node.Id, first.ConversationId, null, 10, CancellationToken);
        Assert.Single(history);
        using var connection = OpenReadOnly(c.DatabasePath); using var command = connection.CreateCommand();
        command.CommandText = "SELECT PathLength,Snr,ReceivedUtc FROM Messages;";
        using var row = command.ExecuteReader(); Assert.True(row.Read());
        Assert.Equal(1, row.GetInt32(0)); Assert.Equal(-1, row.GetDouble(1)); Assert.Equal(original.ReceivedUtc.ToString("O"), row.GetString(2));
        row.Close(); Assert.Equal(2, ScalarInt(connection, "SELECT COUNT(*) FROM IncomingMessageEvents;"));
        await c.Storage.ReadStates.AdvanceAsync(new(c.Node.Id, first.ConversationId, first.MessageId, first.LocalSequence), CancellationToken);
        await c.Storage.IncomingMessages.StoreAsync(retry with { EventId = Guid.NewGuid() }, CancellationToken);
        Assert.Equal(0, (await c.Storage.ReadStates.GetAsync(c.Node.Id, first.ConversationId, CancellationToken)).UnreadCount);
    }

    [Fact]
    public async Task RetryAliasSurvivesReopenAndDirectoryBecomingAmbiguous()
    {
        await using var c = await MessageStorageContext.CreateAsync(); var key = Key(10); await SeedContact(c, key);
        var first = await c.Storage.IncomingMessages.StoreAsync(c.Envelope(Contact(key[..6], "same")), CancellationToken);
        var retry = c.Envelope(Contact(key[..6], "same"));
        await c.Storage.IncomingMessages.StoreAsync(retry, CancellationToken);
        await c.ReopenAsync();
        var other = key.ToArray(); other[^1]++;
        await c.Storage.Directories.ApplySnapshotAsync(c.Node.Id, c.Session.Id,
            [new(key, "First", 1, 0, new byte[64], c.Now, 0, 0), new(other, "Second", 1, 0, new byte[64], c.Now, 0, 0)], [], c.Now, CancellationToken);
        var replay = await c.Storage.IncomingMessages.StoreAsync(retry, CancellationToken);
        Assert.False(replay.Inserted); Assert.Equal(first.MessageId, replay.MessageId);
        Assert.Equal(first.ConversationId, replay.ConversationId);
        var unknown = await c.Storage.IncomingMessages.StoreAsync(retry with { EventId = Guid.NewGuid() }, CancellationToken);
        Assert.True(unknown.Inserted); Assert.NotEqual(first.MessageId, unknown.MessageId);
    }

    [Fact]
    public async Task NewTimestampExactTextAndExtraIdentifySeparateMessages()
    {
        await using var c = await MessageStorageContext.CreateAsync(); var key = Key(10); await SeedContact(c, key);
        var message = Contact(key[..6], "Hello");
        var variants = new[] { message, message with { Timestamp = message.Timestamp.AddSeconds(1) },
            message with { Text = "hello" }, message with { Text = "Hello " },
            message with { SenderPrefix = new byte[] { 1, 2, 3, 4 } } };
        foreach (var variant in variants)
        {
            var first = await c.Storage.IncomingMessages.StoreAsync(c.Envelope(variant), CancellationToken);
            var duplicate = await c.Storage.IncomingMessages.StoreAsync(c.Envelope(variant), CancellationToken);
            Assert.True(first.Inserted); Assert.False(duplicate.Inserted); Assert.Equal(first.MessageId, duplicate.MessageId);
        }
        using var connection = OpenReadOnly(c.DatabasePath); Assert.Equal(5, ScalarInt(connection, "SELECT COUNT(*) FROM Messages;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnresolvedOrAmbiguousPrefixNeverMergesDifferentEvents(bool ambiguous)
    {
        await using var c = await MessageStorageContext.CreateAsync(); var key = Key(10);
        if (ambiguous)
        {
            var other = key.ToArray(); other[^1]++;
            await c.Storage.Directories.ApplySnapshotAsync(c.Node.Id, c.Session.Id,
                [new(key, "First", 1, 0, new byte[64], c.Now, 0, 0), new(other, "Second", 1, 0, new byte[64], c.Now, 0, 0)], [], c.Now, CancellationToken);
        }
        var first = await c.Storage.IncomingMessages.StoreAsync(c.Envelope(Contact(key[..6], "same")), CancellationToken);
        var second = await c.Storage.IncomingMessages.StoreAsync(c.Envelope(Contact(key[..6], "same")), CancellationToken);
        Assert.True(first.Inserted); Assert.True(second.Inserted); Assert.NotEqual(first.MessageId, second.MessageId);
    }

    [Theory]
    [InlineData(MessageTextType.CliData)]
    [InlineData(MessageTextType.SignedPlain)]
    public async Task CliAndRoomPostsKeepExistingBehavior(MessageTextType type)
    {
        await using var c = await MessageStorageContext.CreateAsync(); var key = Key(10); await SeedContact(c, key);
        var message = Contact(key[..6], "same") with { TextType = type, SenderPrefix = new byte[] { 1, 2, 3, 4 } };
        var first = await c.Storage.IncomingMessages.StoreAsync(c.Envelope(message), CancellationToken);
        var second = await c.Storage.IncomingMessages.StoreAsync(c.Envelope(message), CancellationToken);
        Assert.True(first.Inserted); Assert.True(second.Inserted); Assert.NotEqual(first.MessageId, second.MessageId);
    }

    [Fact]
    public async Task NodeAndFullContactIdentityScopeDedup()
    {
        await using var c = await MessageStorageContext.CreateAsync(); var key = Key(10); await SeedContact(c, key);
        var first = await c.Storage.IncomingMessages.StoreAsync(c.Envelope(Contact(key[..6], "same")), CancellationToken);
        var replacement = key.ToArray(); replacement[^1]++;
        await SeedContact(c, replacement); // Full key changed while the received prefix stayed identical.
        var otherContact = await c.Storage.IncomingMessages.StoreAsync(c.Envelope(Contact(key[..6], "same")), CancellationToken);
        Assert.True(otherContact.Inserted); Assert.NotEqual(first.ConversationId, otherContact.ConversationId);
        var node = await c.Storage.Nodes.FindOrCreateAsync(Key(200), "Other", c.Now, CancellationToken);
        var session = c.Session with { Id = Guid.NewGuid(), NodeId = node.Id };
        await c.Storage.Sessions.CreateAsync(session, CancellationToken);
        await c.Storage.Directories.ApplySnapshotAsync(node.Id, session.Id, [new(key, "Peer", 1, 0, new byte[64], c.Now, 0, 0)], [], c.Now, CancellationToken);
        var otherNode = await c.Storage.IncomingMessages.StoreAsync(c.Envelope(Contact(key[..6], "same")) with { NodeId = node.Id, SessionId = session.Id }, CancellationToken);
        Assert.True(otherNode.Inserted); Assert.NotEqual(first.MessageId, otherNode.MessageId);
    }

    [Fact]
    public async Task FailedAliasWritePausesIngressAndRetryDoesNotCreateSecondMessage()
    {
        await using var c = await MessageStorageContext.CreateAsync(); var key = Key(10); await SeedContact(c, key);
        var original = c.Envelope(Contact(key[..6], "same"));
        var first = await c.Storage.IncomingMessages.StoreAsync(original, CancellationToken);
        void Execute(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={c.DatabasePath};Pooling=False"); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        Execute("CREATE TRIGGER RejectIncomingAlias BEFORE INSERT ON IncomingMessageEvents BEGIN SELECT RAISE(ABORT,'alias failure'); END;");
        await using var ingress = new MessageIngestor(c.Storage.IncomingMessages, TimeProvider.System);
        var retry = original with { EventId = Guid.NewGuid() };
        var commits = new List<StoredIncomingMessage>(); ingress.MessageCommitted += (_, commit) => commits.Add(commit.Message);
        await ingress.EnqueueAsync(retry, CancellationToken);
        await Assert.ThrowsAsync<ReceiveIngestException>(() => ingress.FlushAsync(CancellationToken));
        Assert.True(ingress.IsPaused); Assert.Empty(commits);
        using (var connection = OpenReadOnly(c.DatabasePath)) { Assert.Equal(1, ScalarInt(connection, "SELECT COUNT(*) FROM Messages;")); Assert.Equal(1, ScalarInt(connection, "SELECT COUNT(*) FROM IncomingMessageEvents;")); }
        Execute("DROP TRIGGER RejectIncomingAlias;");
        await ingress.RetryAsync(CancellationToken); await ingress.FlushAsync(CancellationToken);
        var committed = Assert.Single(commits); Assert.False(committed.Inserted); Assert.Equal(first.MessageId, committed.MessageId);
        await c.Storage.HistoryClear.ClearAsync(c.Node.Id, first.ConversationId, CancellationToken);
        using (var connection = OpenReadOnly(c.DatabasePath)) Assert.Equal(0, ScalarInt(connection, "SELECT COUNT(*) FROM IncomingMessageEvents;"));
        // Clear intentionally resets dedup; a packet heard later can be stored again.
        var afterClear = await c.Storage.IncomingMessages.StoreAsync(retry, CancellationToken);
        Assert.True(afterClear.Inserted); Assert.NotEqual(first.MessageId, afterClear.MessageId);
    }
}
