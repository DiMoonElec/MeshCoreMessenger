using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class HistoryPagingTests
{
    [Fact]
    public async Task ReadsBeginningEndAndAroundPositionWithExplicitBoundaries()
    {
        await using var context = await HistoryContext.CreateAsync(7);

        var latest = await context.Storage.History.GetMessagesBeforeAsync(
            context.NodeId, context.ConversationId, null, 3, CancellationToken);
        Assert.Equal([5, 6, 7], Numbers(latest));
        Assert.True(latest.HasEarlier);
        Assert.False(latest.HasLater);

        var older = await context.Storage.History.GetMessagesBeforeAsync(
            context.NodeId, context.ConversationId, latest.FirstPosition, 3, CancellationToken);
        Assert.Equal([2, 3, 4], Numbers(older));
        Assert.True(older.HasEarlier);
        Assert.True(older.HasLater);

        var earliest = await context.Storage.History.GetMessagesAfterAsync(
            context.NodeId, context.ConversationId, null, 3, CancellationToken);
        Assert.Equal([1, 2, 3], Numbers(earliest));
        Assert.False(earliest.HasEarlier);
        Assert.True(earliest.HasLater);

        var anchor = await context.Storage.History.GetMessagePositionAsync(
            context.NodeId,
            context.ConversationId,
            context.MessageIds[3],
            CancellationToken);
        var around = await context.Storage.History.GetMessagesAroundAsync(
            Assert.IsType<HistoryMessagePosition>(anchor), 2, 2, CancellationToken);
        Assert.Equal([2, 3, 4, 5, 6], Numbers(around));
        Assert.True(around.HasEarlier);
        Assert.True(around.HasLater);

        var empty = await context.Storage.History.GetMessagesAfterAsync(
            context.NodeId, context.EmptyConversationId, null, 10, CancellationToken);
        Assert.Empty(empty.Items);
        Assert.Null(empty.FirstPosition);
        Assert.Null(empty.LastPosition);
        Assert.False(empty.HasEarlier);
        Assert.False(empty.HasLater);
    }

    [Fact]
    public async Task GlobalSequenceGapsAndIdenticalTimestampsDoNotLoseOrDuplicateMessages()
    {
        await using var context = await HistoryContext.CreateAsync(8, interleaveOtherConversation: true);

        var first = await context.Storage.History.GetMessagesAfterAsync(
            context.NodeId, context.ConversationId, null, 3, CancellationToken);
        var second = await context.Storage.History.GetMessagesAfterAsync(
            context.NodeId, context.ConversationId, first.LastPosition, 3, CancellationToken);
        var third = await context.Storage.History.GetMessagesAfterAsync(
            context.NodeId, context.ConversationId, second.LastPosition, 3, CancellationToken);
        var all = first.Items.Concat(second.Items).Concat(third.Items).ToArray();

        Assert.Equal(Enumerable.Range(1, 8), all.Select(MessageNumber));
        Assert.Equal(8, all.Select(item => item.Id).Distinct().Count());
        Assert.Contains(all.Zip(all.Skip(1)), pair =>
            pair.Second.LocalSequence - pair.First.LocalSequence > 1);
        Assert.Single(all.Select(item => item.ReceivedUtc).Distinct());
    }

    [Fact]
    public async Task PositionIsStrictlyScopedAndMustMatchMessageIdAndSequence()
    {
        await using var context = await HistoryContext.CreateAsync(3);
        var position = Assert.IsType<HistoryMessagePosition>(
            await context.Storage.History.GetMessagePositionAsync(
                context.NodeId, context.ConversationId, context.MessageIds[1], CancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() => context.Storage.History.GetMessagesBeforeAsync(
            Guid.NewGuid(), context.ConversationId, position, 2, CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => context.Storage.History.GetMessagesAfterAsync(
            context.NodeId, context.EmptyConversationId, position, 2, CancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => context.Storage.History.GetMessagesAroundAsync(
            position with { MessageId = Guid.NewGuid() }, 1, 1, CancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => context.Storage.History.GetMessagesAroundAsync(
            position with { LocalSequence = position.LocalSequence + 1 }, 1, 1, CancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => context.Storage.History.GetMessagesBeforeAsync(
            Guid.NewGuid(), context.ConversationId, null, 2, CancellationToken));
    }

    [Fact]
    public async Task ProjectionPreservesTextBinaryResolutionAndMissingWireTimestampMetadata()
    {
        await using var context = await HistoryContext.CreateAsync(4, richMetadata: true);

        var page = await context.Storage.History.GetMessagesAfterAsync(
            context.NodeId, context.ConversationId, null, 10, CancellationToken);

        Assert.Equal(0, page.Items[0].TextType);
        Assert.Null(page.Items[0].WireTimestamp);
        Assert.Equal(1, page.Items[1].TextType);
        Assert.Equal(2, page.Items[2].TextType);
        Assert.Equal(MessageResolutionState.Ambiguous, page.Items[2].ResolutionState);
        Assert.Equal(StoredMessageKind.Binary, page.Items[3].MessageKind);
        Assert.Equal((ushort)42, page.Items[3].BinaryDataType);
        Assert.Null(page.Items[3].Text);
        Assert.All(page.Items, item => Assert.NotEqual(default, item.ReceivedUtc));
    }

    [Fact]
    public async Task AppendBetweenPagesAppearsAfterStableBoundaryWithoutDuplicates()
    {
        await using var context = await HistoryContext.CreateAsync(3);
        var first = await context.Storage.History.GetMessagesAfterAsync(
            context.NodeId, context.ConversationId, null, 2, CancellationToken);

        await context.AppendAsync(4);
        var second = await context.Storage.History.GetMessagesAfterAsync(
            context.NodeId, context.ConversationId, first.LastPosition, 2, CancellationToken);

        Assert.Equal([1, 2], Numbers(first));
        Assert.Equal([3, 4], Numbers(second));
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));
        Assert.False(second.HasLater);
    }

    [Fact]
    public async Task CancellationAndBoundsAreEnforcedWithoutDamagingFollowingReads()
    {
        await using var context = await HistoryContext.CreateAsync(3);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Storage.History.GetMessagesAfterAsync(
                context.NodeId, context.ConversationId, null, 10, cancellation.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            context.Storage.History.GetMessagesBeforeAsync(
                context.NodeId, context.ConversationId, null, 501, CancellationToken));
        var position = Assert.IsType<HistoryMessagePosition>(
            await context.Storage.History.GetMessagePositionAsync(
                context.NodeId, context.ConversationId, context.MessageIds[0], CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            context.Storage.History.GetMessagesAroundAsync(position, 250, 250, CancellationToken));

        Assert.Equal(3, (await context.Storage.History.GetMessagesAfterAsync(
            context.NodeId, context.ConversationId, null, 10, CancellationToken)).Items.Count);
    }

    [Fact]
    public async Task HundredThousandMessagesUseSequenceIndexAndNeverMaterializeMoreThanFiveHundred()
    {
        await using var context = await HistoryContext.CreateAsync(100_000);

        var latest = await context.Storage.History.GetMessagesBeforeAsync(
            context.NodeId, context.ConversationId, null, 500, CancellationToken);
        Assert.Equal(500, latest.Items.Count);
        Assert.Equal(99_501, MessageNumber(latest.Items[0]));
        Assert.Equal(100_000, MessageNumber(latest.Items[^1]));
        Assert.True(latest.HasEarlier);

        var centerPosition = Assert.IsType<HistoryMessagePosition>(
            await context.Storage.History.GetMessagePositionAsync(
                context.NodeId, context.ConversationId, context.MessageIds[49_999], CancellationToken));
        var around = await context.Storage.History.GetMessagesAroundAsync(
            centerPosition, 249, 250, CancellationToken);
        Assert.Equal(500, around.Items.Count);
        Assert.Equal(49_751, MessageNumber(around.Items[0]));
        Assert.Equal(50_250, MessageNumber(around.Items[^1]));
        Assert.True(around.HasEarlier);
        Assert.True(around.HasLater);
        Assert.Contains("IX_Messages_Conversation_Sequence", context.QueryPlan, StringComparison.Ordinal);
    }

    private static int[] Numbers(HistoryMessagePage page) =>
        page.Items.Select(MessageNumber).ToArray();

    private static int MessageNumber(HistoryMessage message) =>
        int.Parse(message.Text![8..], System.Globalization.CultureInfo.InvariantCulture);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class HistoryContext : IAsyncDisposable
    {
        private readonly TemporaryDirectory _temporary;

        private HistoryContext(
            TemporaryDirectory temporary,
            LocalStorage storage,
            Guid nodeId,
            Guid conversationId,
            Guid emptyConversationId,
            IReadOnlyList<Guid> messageIds,
            string queryPlan)
        {
            _temporary = temporary;
            Storage = storage;
            NodeId = nodeId;
            ConversationId = conversationId;
            EmptyConversationId = emptyConversationId;
            MessageIds = messageIds;
            QueryPlan = queryPlan;
        }

        public LocalStorage Storage { get; }
        public Guid NodeId { get; }
        public Guid ConversationId { get; }
        public Guid EmptyConversationId { get; }
        public IReadOnlyList<Guid> MessageIds { get; }
        public string QueryPlan { get; }

        public static async Task<HistoryContext> CreateAsync(
            int messageCount,
            bool interleaveOtherConversation = false,
            bool richMetadata = false)
        {
            var temporary = new TemporaryDirectory();
            var nodeId = Guid.NewGuid();
            var conversationId = Guid.NewGuid();
            var otherConversationId = Guid.NewGuid();
            var emptyConversationId = Guid.NewGuid();
            var messageIds = Enumerable.Range(1, messageCount).Select(GuidFromNumber).ToArray();
            string queryPlan;
            await using (var writer = await DatabaseWorker.OpenAsync(
                temporary.Paths.DatabasePath, CancellationToken))
            {
                queryPlan = await writer.ExecuteAsync(connection =>
                {
                    using var transaction = connection.BeginTransaction();
                    InsertNode(connection, transaction, nodeId);
                    InsertConversation(connection, transaction, nodeId, conversationId, 0x11);
                    InsertConversation(connection, transaction, nodeId, otherConversationId, 0x22);
                    InsertConversation(connection, transaction, nodeId, emptyConversationId, 0x33);
                    for (var index = 1; index <= messageCount; index++)
                    {
                        InsertMessage(
                            connection,
                            transaction,
                            conversationId,
                            messageIds[index - 1],
                            index,
                            richMetadata);
                        if (interleaveOtherConversation)
                        {
                            InsertMessage(
                                connection,
                                transaction,
                                otherConversationId,
                                Guid.NewGuid(),
                                -index,
                                richMetadata: false);
                        }
                    }

                    transaction.Commit();
                    using var explain = connection.CreateCommand();
                    explain.CommandText = """
                        EXPLAIN QUERY PLAN
                        SELECT Id, LocalSequence
                        FROM Messages
                        WHERE ConversationId = $conversationId AND LocalSequence < $sequence
                        ORDER BY LocalSequence DESC
                        LIMIT 500;
                        """;
                    explain.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
                    explain.Parameters.AddWithValue("$sequence", long.MaxValue);
                    var details = new List<string>();
                    using var rows = explain.ExecuteReader();
                    while (rows.Read())
                    {
                        details.Add(rows.GetString(3));
                    }

                    return string.Join(Environment.NewLine, details);
                }, CancellationToken);
            }

            var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
            return new HistoryContext(
                temporary,
                storage,
                nodeId,
                conversationId,
                emptyConversationId,
                messageIds,
                queryPlan);
        }

        public async Task AppendAsync(int number)
        {
            await using var writer = await DatabaseWorker.OpenAsync(
                _temporary.Paths.DatabasePath, CancellationToken);
            await writer.ExecuteAsync(connection =>
            {
                using var transaction = connection.BeginTransaction();
                InsertMessage(
                    connection,
                    transaction,
                    ConversationId,
                    GuidFromNumber(number),
                    number,
                    richMetadata: false);
                transaction.Commit();
                return true;
            }, CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await Storage.DisposeAsync();
            _temporary.Dispose();
        }

        private static void InsertNode(SqliteConnection connection, SqliteTransaction transaction, Guid nodeId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Nodes (Id, PublicKey, LastName, FirstSeenUtc, LastSeenUtc)
                VALUES ($id, $key, 'Paging node', $time, $time);
                """;
            command.Parameters.AddWithValue("$id", nodeId.ToString("D"));
            command.Parameters.Add("$key", SqliteType.Blob).Value = Enumerable.Repeat((byte)0xA5, 32).ToArray();
            command.Parameters.AddWithValue("$time", "2026-09-30T00:00:00.0000000+00:00");
            command.ExecuteNonQuery();
        }

        private static void InsertConversation(
            SqliteConnection connection,
            SqliteTransaction transaction,
            Guid nodeId,
            Guid conversationId,
            byte identity)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Conversations (
                    Id, NodeId, Kind, UnknownIdentity, Title, IsArchived,
                    LastReadSequence, CreatedUtc, UpdatedUtc)
                VALUES ($id, $nodeId, 2, $identity, 'Paging', 0, 0, $time, $time);
                """;
            command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
            command.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
            command.Parameters.Add("$identity", SqliteType.Blob).Value = Enumerable.Repeat(identity, 6).ToArray();
            command.Parameters.AddWithValue("$time", "2026-09-30T00:00:00.0000000+00:00");
            command.ExecuteNonQuery();
        }

        private static void InsertMessage(
            SqliteConnection connection,
            SqliteTransaction transaction,
            Guid conversationId,
            Guid messageId,
            int number,
            bool richMetadata)
        {
            var binary = richMetadata && number == 4;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Messages (
                    Id, ConversationId, Direction, MessageKind, TextType, BinaryDataType,
                    Text, Payload, ReceivedUtc, WireTimestamp, ResolutionState)
                VALUES (
                    $id, $conversationId, 0, $messageKind, $textType, $binaryDataType,
                    $text, $payload, $receivedUtc, $wireTimestamp, $resolutionState);
                """;
            command.Parameters.AddWithValue("$id", messageId.ToString("D"));
            command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
            command.Parameters.AddWithValue("$messageKind", (int)(binary ? StoredMessageKind.Binary : StoredMessageKind.Text));
            command.Parameters.Add("$textType", SqliteType.Integer).Value = binary
                ? DBNull.Value
                : richMetadata ? Math.Min(number - 1, 2) : 0;
            command.Parameters.Add("$binaryDataType", SqliteType.Integer).Value = binary ? 42 : DBNull.Value;
            command.Parameters.Add("$text", SqliteType.Text).Value = binary
                ? DBNull.Value
                : $"message {number}";
            command.Parameters.Add("$payload", SqliteType.Blob).Value = binary ? new byte[] { 1, 2, 3 } : DBNull.Value;
            command.Parameters.AddWithValue("$receivedUtc", "2026-09-30T12:00:00.0000000+00:00");
            command.Parameters.Add("$wireTimestamp", SqliteType.Integer).Value = number == 1
                ? DBNull.Value
                : 1_800_000_000L + number;
            command.Parameters.AddWithValue(
                "$resolutionState",
                (int)(richMetadata && number == 3
                    ? MessageResolutionState.Ambiguous
                    : MessageResolutionState.Resolved));
            command.ExecuteNonQuery();
        }

        private static Guid GuidFromNumber(int number)
        {
            var bytes = new byte[16];
            BitConverter.TryWriteBytes(bytes, number);
            bytes[15] = 0xC4;
            return new Guid(bytes);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "MeshCoreMessenger.HistoryPaging.Tests",
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
