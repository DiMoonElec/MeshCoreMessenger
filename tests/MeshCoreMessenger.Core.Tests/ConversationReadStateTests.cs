using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class ConversationReadStateTests
{
    [Fact]
    public async Task CountsOnlyIncomingAndAdvancesMonotonicallyThroughExactPositions()
    {
        await using var context = await ReadContext.CreateAsync();

        var initial = await context.Storage.ReadStates.GetAsync(
            context.NodeId, context.ConversationId, CancellationToken);
        Assert.Equal(4, initial.UnreadCount);
        Assert.Equal(context.Positions[0], initial.FirstUnreadPosition);

        var throughOutgoing = await context.Storage.ReadStates.AdvanceAsync(
            context.Positions[2], CancellationToken);
        Assert.Equal(context.Positions[2].LocalSequence, throughOutgoing.LastReadSequence);
        Assert.Equal(2, throughOutgoing.UnreadCount);
        Assert.Equal(context.Positions[3], throughOutgoing.FirstUnreadPosition);

        var reverse = await context.Storage.ReadStates.AdvanceAsync(
            context.Positions[1], CancellationToken);
        Assert.Equal(throughOutgoing, reverse);

        var repeated = await context.Storage.ReadStates.AdvanceAsync(
            context.Positions[2], CancellationToken);
        Assert.Equal(throughOutgoing, repeated);

        var complete = await context.Storage.ReadStates.AdvanceAsync(
            context.Positions[^1], CancellationToken);
        Assert.Equal(0, complete.UnreadCount);
        Assert.Null(complete.FirstUnreadPosition);
    }

    [Fact]
    public async Task ReadStateIsNodeScopedAndRequiresAnExactPersistedPosition()
    {
        await using var context = await ReadContext.CreateAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => context.Storage.ReadStates.GetAsync(
            Guid.NewGuid(), context.ConversationId, CancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => context.Storage.ReadStates.AdvanceAsync(
            context.Positions[0] with { NodeId = Guid.NewGuid() }, CancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => context.Storage.ReadStates.AdvanceAsync(
            context.Positions[0] with { MessageId = Guid.NewGuid() }, CancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => context.Storage.ReadStates.AdvanceAsync(
            context.Positions[0] with { LocalSequence = context.Positions[0].LocalSequence + 1 },
            CancellationToken));
    }

    [Fact]
    public async Task WatermarkAndDirectoryUnreadProjectionSurviveRestart()
    {
        await using var context = await ReadContext.CreateAsync();
        var paths = context.Paths;
        var nodeId = context.NodeId;
        var conversationId = context.ConversationId;
        await context.Storage.ReadStates.AdvanceAsync(context.Positions[3], CancellationToken);
        await context.Storage.DisposeAsync();

        await using (var reopened = await LocalStorage.OpenAsync(paths, CancellationToken))
        {
            var state = await reopened.ReadStates.GetAsync(nodeId, conversationId, CancellationToken);
            Assert.Equal(1, state.UnreadCount);
            var directory = await reopened.ConversationDirectory.GetPageAsync(
                nodeId,
                ConversationDirectorySection.UnknownContacts,
                null,
                10,
                CancellationToken);
            Assert.Equal(1, Assert.Single(directory.Items).UnreadCount);
        }
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class ReadContext : IAsyncDisposable
    {
        private readonly TemporaryDirectory _temporary;
        private int _disposed;

        private ReadContext(
            TemporaryDirectory temporary,
            LocalStorage storage,
            Guid nodeId,
            Guid conversationId,
            IReadOnlyList<HistoryMessagePosition> positions)
        {
            _temporary = temporary;
            Storage = storage;
            NodeId = nodeId;
            ConversationId = conversationId;
            Positions = positions;
        }

        public LocalStorage Storage { get; }
        public TestAppPaths Paths => _temporary.Paths;
        public Guid NodeId { get; }
        public Guid ConversationId { get; }
        public IReadOnlyList<HistoryMessagePosition> Positions { get; }

        public static async Task<ReadContext> CreateAsync()
        {
            var temporary = new TemporaryDirectory();
            var nodeId = Guid.NewGuid();
            var conversationId = Guid.NewGuid();
            var messageIds = Enumerable.Range(1, 5).Select(_ => Guid.NewGuid()).ToArray();
            var sequences = new List<long>();
            await using (var writer = await DatabaseWorker.OpenAsync(
                temporary.Paths.DatabasePath, CancellationToken))
            {
                await writer.ExecuteAsync(connection =>
                {
                    using var transaction = connection.BeginTransaction();
                    using (var node = connection.CreateCommand())
                    {
                        node.Transaction = transaction;
                        node.CommandText = """
                            INSERT INTO Nodes (Id, PublicKey, LastName, FirstSeenUtc, LastSeenUtc)
                            VALUES ($id, $key, 'Read node', $time, $time);
                            """;
                        node.Parameters.AddWithValue("$id", nodeId.ToString("D"));
                        node.Parameters.Add("$key", SqliteType.Blob).Value = Enumerable.Repeat((byte)0x66, 32).ToArray();
                        node.Parameters.AddWithValue("$time", "2026-09-30T00:00:00.0000000+00:00");
                        node.ExecuteNonQuery();
                    }

                    using (var conversation = connection.CreateCommand())
                    {
                        conversation.Transaction = transaction;
                        conversation.CommandText = """
                            INSERT INTO Conversations (
                                Id, NodeId, Kind, UnknownIdentity, Title, IsArchived,
                                LastReadSequence, CreatedUtc, UpdatedUtc)
                            VALUES ($id, $nodeId, 2, $identity, 'Unread', 0, 0, $time, $time);
                            """;
                        conversation.Parameters.AddWithValue("$id", conversationId.ToString("D"));
                        conversation.Parameters.AddWithValue("$nodeId", nodeId.ToString("D"));
                        conversation.Parameters.Add("$identity", SqliteType.Blob).Value = new byte[] { 1, 2, 3, 4, 5, 6 };
                        conversation.Parameters.AddWithValue("$time", "2026-09-30T00:00:00.0000000+00:00");
                        conversation.ExecuteNonQuery();
                    }

                    var directions = new[]
                    {
                        MessageDirection.Incoming,
                        MessageDirection.Incoming,
                        MessageDirection.Outgoing,
                        MessageDirection.Incoming,
                        MessageDirection.Incoming,
                    };
                    for (var index = 0; index < directions.Length; index++)
                    {
                        using var message = connection.CreateCommand();
                        message.Transaction = transaction;
                        message.CommandText = """
                            INSERT INTO Messages (
                                Id, ConversationId, Direction, MessageKind, Text,
                                ReceivedUtc, ResolutionState)
                            VALUES ($id, $conversationId, $direction, 0, $text, $time, 1);
                            SELECT last_insert_rowid();
                            """;
                        message.Parameters.AddWithValue("$id", messageIds[index].ToString("D"));
                        message.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
                        message.Parameters.AddWithValue("$direction", (int)directions[index]);
                        message.Parameters.AddWithValue("$text", $"message {index + 1}");
                        message.Parameters.AddWithValue("$time", $"2026-09-30T00:00:0{index}.0000000+00:00");
                        sequences.Add((long)message.ExecuteScalar()!);
                    }

                    transaction.Commit();
                    return true;
                }, CancellationToken);
            }

            var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
            var positions = messageIds.Select((id, index) =>
                new HistoryMessagePosition(nodeId, conversationId, id, sequences[index])).ToArray();
            return new ReadContext(temporary, storage, nodeId, conversationId, positions);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await Storage.DisposeAsync();
            _temporary.Dispose();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "MeshCoreMessenger.ReadState.Tests",
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
