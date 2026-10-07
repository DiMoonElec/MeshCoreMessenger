using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class LocalHistoryReaderTests
{
    [Fact]
    public async Task EmptyDatabaseReturnsEmptyHistory()
    {
        using var temporary = new TemporaryDirectory();
        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        var nodeId = Guid.NewGuid();

        Assert.Empty(await storage.History.GetConversationsAsync(nodeId, 100, CancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => storage.History.GetMessagesAsync(
            nodeId,
            Guid.NewGuid(),
            null,
            100,
            CancellationToken));
    }

    [Fact]
    public async Task ReadsConversationSummariesByLatestActivityWithoutMixingNodes()
    {
        using var temporary = new TemporaryDirectory();
        var firstNode = Guid.NewGuid();
        var secondNode = Guid.NewGuid();
        var oldConversation = Guid.NewGuid();
        var latestConversation = Guid.NewGuid();
        var emptyConversation = Guid.NewGuid();
        await SeedAsync(
            temporary.Paths,
            [
                new SeedConversation(oldConversation, firstNode, "Old", 0x11, "old message"),
                new SeedConversation(latestConversation, secondNode, "Latest", 0x22, "latest message"),
                new SeedConversation(emptyConversation, firstNode, "Empty", 0x33),
            ]);

        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        var conversations = await storage.History.GetConversationsAsync(firstNode, 10, CancellationToken);

        Assert.Collection(
            conversations,
            item =>
            {
                Assert.Equal(oldConversation, item.Id);
                Assert.Equal(firstNode, item.NodeId);
                Assert.Equal("old message", item.LastMessageText);
                Assert.Equal(MessageDirection.Incoming, item.LastMessageDirection);
                Assert.Equal(StoredMessageKind.Text, item.LastMessageKind);
            },
            item =>
            {
                Assert.Equal(emptyConversation, item.Id);
                Assert.Equal(firstNode, item.NodeId);
                Assert.Null(item.LastMessageSequence);
                Assert.Null(item.LastMessageText);
            });

        var secondNodeConversations = await storage.History.GetConversationsAsync(
            secondNode,
            10,
            CancellationToken);
        var secondNodeConversation = Assert.Single(secondNodeConversations);
        Assert.Equal(latestConversation, secondNodeConversation.Id);
        Assert.Equal("Latest", secondNodeConversation.Title);
        Assert.Equal("latest message", secondNodeConversation.LastMessageText);
    }

    [Fact]
    public async Task ReadsBoundedMessagePagesInChronologicalOrder()
    {
        using var temporary = new TemporaryDirectory();
        var node = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        var otherConversation = Guid.NewGuid();
        await SeedAsync(
            temporary.Paths,
            [
                new SeedConversation(conversation, node, "Paged", 0x44, "one", "two", "three", "four"),
                new SeedConversation(otherConversation, node, "Other", 0x55, "do not include"),
            ]);

        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        var latest = await storage.History.GetMessagesAsync(node, conversation, null, 2, CancellationToken);
        var older = await storage.History.GetMessagesAsync(
            node,
            conversation,
            latest[0].LocalSequence,
            2,
            CancellationToken);

        Assert.Equal(["three", "four"], latest.Select(item => item.Text));
        Assert.Equal(["one", "two"], older.Select(item => item.Text));
        Assert.All(latest.Concat(older), item => Assert.Equal(conversation, item.ConversationId));
        Assert.True(latest[0].LocalSequence < latest[1].LocalSequence);
        Assert.True(older[0].LocalSequence < older[1].LocalSequence);
    }

    [Fact]
    public async Task HistorySurvivesStorageReopen()
    {
        using var temporary = new TemporaryDirectory();
        var node = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        await SeedAsync(
            temporary.Paths,
            [new SeedConversation(conversation, node, "Persisted", 0x66, "still here")]);

        await using (var first = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken))
        {
            Assert.Single(await first.History.GetConversationsAsync(node, 10, CancellationToken));
        }

        await using var reopened = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        var messages = await reopened.History.GetMessagesAsync(node, conversation, null, 10, CancellationToken);
        Assert.Single(messages);
        Assert.Equal("still here", messages[0].Text);
    }

    [Fact]
    public async Task ReadCancellationDoesNotDamageSubsequentReads()
    {
        using var temporary = new TemporaryDirectory();
        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.History.GetConversationsAsync(Guid.NewGuid(), 10, cancellation.Token));

        Assert.Empty(await storage.History.GetConversationsAsync(Guid.NewGuid(), 10, CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task RejectsUnboundedPageSizes(int limit)
    {
        using var temporary = new TemporaryDirectory();
        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => storage.History.GetConversationsAsync(Guid.NewGuid(), limit, CancellationToken));
    }

    [Fact]
    public async Task RejectsConversationOwnedByAnotherNode()
    {
        using var temporary = new TemporaryDirectory();
        var ownerNode = Guid.NewGuid();
        var foreignNode = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        await SeedAsync(
            temporary.Paths,
            [new SeedConversation(conversation, ownerNode, "Private", 0x77, "secret")]);

        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => storage.History.GetMessagesAsync(
            foreignNode,
            conversation,
            null,
            10,
            CancellationToken));
    }

    [Fact]
    public async Task ReadsKnownNodesInBoundedLastSeenOrderIncludingSameNames()
    {
        using var temporary = new TemporaryDirectory();
        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        await storage.Nodes.FindOrCreateAsync(
            Enumerable.Repeat((byte)0x10, 32).ToArray(),
            "Same name",
            DateTimeOffset.Parse("2026-09-25T10:00:00Z"),
            CancellationToken);
        var mostRecent = await storage.Nodes.FindOrCreateAsync(
            Enumerable.Repeat((byte)0x20, 32).ToArray(),
            "Same name",
            DateTimeOffset.Parse("2026-09-25T11:00:00Z"),
            CancellationToken);
        var nodes = await storage.Nodes.GetAllAsync(1, CancellationToken);

        Assert.Single(nodes);
        Assert.Equal(mostRecent.Id, nodes[0].Id);
        Assert.Equal("Same name", nodes[0].LastName);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => storage.Nodes.GetAllAsync(501, CancellationToken));
    }

    private static async Task SeedAsync(IAppPaths paths, IReadOnlyList<SeedConversation> conversations)
    {
        await using var writer = await DatabaseWorker.OpenAsync(paths.DatabasePath, CancellationToken);
        await writer.ExecuteAsync(connection =>
        {
            foreach (var node in conversations.Select(item => item.NodeId).Distinct())
            {
                using var nodeCommand = connection.CreateCommand();
                nodeCommand.CommandText = """
                    INSERT INTO Nodes (Id, PublicKey, LastName, FirstSeenUtc, LastSeenUtc)
                    VALUES ($id, $publicKey, $name, $firstSeenUtc, $lastSeenUtc);
                    """;
                nodeCommand.Parameters.AddWithValue("$id", node.ToString("D"));
                nodeCommand.Parameters.Add("$publicKey", SqliteType.Blob).Value = CreateNodeKey(node);
                nodeCommand.Parameters.AddWithValue("$name", $"Node {node:N}");
                nodeCommand.Parameters.AddWithValue("$firstSeenUtc", "2026-09-25T10:00:00.0000000+00:00");
                nodeCommand.Parameters.AddWithValue("$lastSeenUtc", "2026-09-25T10:00:00.0000000+00:00");
                nodeCommand.ExecuteNonQuery();
            }

            var messageIndex = 0;
            foreach (var conversation in conversations)
            {
                using (var conversationCommand = connection.CreateCommand())
                {
                    conversationCommand.CommandText = """
                        INSERT INTO Conversations (
                            Id, NodeId, Kind, UnknownIdentity, Title, IsArchived,
                            LastReadSequence, CreatedUtc, UpdatedUtc)
                        VALUES (
                            $id, $nodeId, $kind, $unknownIdentity, $title, 0,
                            0, $createdUtc, $updatedUtc);
                        """;
                    conversationCommand.Parameters.AddWithValue("$id", conversation.Id.ToString("D"));
                    conversationCommand.Parameters.AddWithValue("$nodeId", conversation.NodeId.ToString("D"));
                    conversationCommand.Parameters.AddWithValue("$kind", (int)ConversationKind.UnknownContact);
                    conversationCommand.Parameters.Add("$unknownIdentity", SqliteType.Blob).Value =
                        Enumerable.Repeat(conversation.IdentityByte, 6).ToArray();
                    conversationCommand.Parameters.AddWithValue("$title", conversation.Title);
                    conversationCommand.Parameters.AddWithValue("$createdUtc", "2026-09-25T10:00:00.0000000+00:00");
                    conversationCommand.Parameters.AddWithValue(
                        "$updatedUtc",
                        $"2026-09-25T10:{messageIndex:D2}:00.0000000+00:00");
                    conversationCommand.ExecuteNonQuery();
                }

                foreach (var text in conversation.Messages)
                {
                    messageIndex++;
                    using var messageCommand = connection.CreateCommand();
                    messageCommand.CommandText = """
                        INSERT INTO Messages (
                            Id, ConversationId, Direction, MessageKind, Text,
                            ReceivedUtc, ResolutionState)
                        VALUES (
                            $id, $conversationId, $direction, $messageKind, $text,
                            $receivedUtc, $resolutionState);
                        """;
                    messageCommand.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                    messageCommand.Parameters.AddWithValue("$conversationId", conversation.Id.ToString("D"));
                    messageCommand.Parameters.AddWithValue("$direction", (int)MessageDirection.Incoming);
                    messageCommand.Parameters.AddWithValue("$messageKind", (int)StoredMessageKind.Text);
                    messageCommand.Parameters.AddWithValue("$text", text);
                    messageCommand.Parameters.AddWithValue(
                        "$receivedUtc",
                        $"2026-09-25T11:{messageIndex:D2}:00.0000000+00:00");
                    messageCommand.Parameters.AddWithValue("$resolutionState", (int)MessageResolutionState.Unresolved);
                    messageCommand.ExecuteNonQuery();
                }
            }

            return true;
        }, CancellationToken);
    }

    private static byte[] CreateNodeKey(Guid id)
    {
        var key = new byte[32];
        id.TryWriteBytes(key);
        id.TryWriteBytes(key.AsSpan(16));
        return key;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed record SeedConversation(
        Guid Id,
        Guid NodeId,
        string Title,
        byte IdentityByte,
        params string?[] OptionalMessages)
    {
        public IReadOnlyList<string> Messages { get; } = OptionalMessages?.OfType<string>().ToArray() ?? [];
    }

    [Fact]
    public async Task ExactMessageDetailsDoNotUseLatestPreviewOrMixNodeAndConversationScopes()
    {
        using var temporary = new TemporaryDirectory();
        var node = Guid.NewGuid(); var conversation = Guid.NewGuid();
        await SeedAsync(temporary.Paths, [new SeedConversation(conversation, node, "Name", 0x66, "earlier", "latest")]);
        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        var messages = await storage.History.GetMessagesAsync(node, conversation, null, 10, CancellationToken);
        var details = await storage.MessageDetails.GetAsync(node, conversation, messages[0].Id, CancellationToken);
        Assert.NotNull(details);
        Assert.Equal("earlier", details.Text);
        Assert.Equal(messages[0].LocalSequence, details.LocalSequence);
        Assert.Null(await storage.MessageDetails.GetAsync(Guid.NewGuid(), conversation, messages[0].Id, CancellationToken));
        Assert.Null(await storage.MessageDetails.GetAsync(node, Guid.NewGuid(), messages[0].Id, CancellationToken));
        Assert.Null(await storage.MessageDetails.GetAsync(node, conversation, Guid.NewGuid(), CancellationToken));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "MeshCoreMessenger.History.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Paths = new TestAppPaths(Path);
        }

        public string Path { get; }
        public TestAppPaths Paths { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private sealed record TestAppPaths(string DataDirectory) : IAppPaths
    {
        public string DatabasePath { get; } = System.IO.Path.Combine(DataDirectory, "messenger.db");
        public string BackupsDirectory { get; } = System.IO.Path.Combine(DataDirectory, "backups");
    }
}
