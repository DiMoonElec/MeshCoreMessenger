using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class OutgoingMessageStoreTests
{
    [Fact]
    public async Task PrepareIsIdempotentAndNotifiesAfterCommitWithoutUnread()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request();
        var commits = new List<OutgoingMessageCommit>();
        Task<IReadOnlyList<HistoryMessage>>? committedHistory = null;
        fixture.Storage.OutgoingMessages.MessageCommitted += (_, commit) =>
        {
            commits.Add(commit);
            committedHistory = fixture.Storage.History.GetMessagesAsync(fixture.NodeId, fixture.ConversationId, null, 10, CancellationToken);
        };
        fixture.Storage.OutgoingMessages.MessageCommitted += (_, _) => throw new InvalidOperationException("Broken subscriber");
        var result = await fixture.Storage.OutgoingMessages.PrepareAsync(request, CancellationToken);
        var history = Assert.Single(await committedHistory!);
        Assert.Equal(result.MessageId, history.Id);
        Assert.Equal(request.OriginalText, history.Text);
        Assert.Equal(SendAttemptState.Prepared, history.LatestAttempt!.State);
        Assert.Equal(result, await fixture.Storage.OutgoingMessages.PrepareAsync(request, CancellationToken));
        Assert.Single(commits);
        var states = await fixture.Storage.ReadStates.GetAsync(fixture.NodeId, fixture.ConversationId, CancellationToken);
        Assert.Equal(0, states.UnreadCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Storage.OutgoingMessages.PrepareAsync(request with { TransmissionText = "different" }, CancellationToken));
        Assert.Single(await fixture.Storage.History.GetMessagesAsync(fixture.NodeId, fixture.ConversationId, null, 10, CancellationToken));
        using var connection = fixture.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TransmissionText FROM Messages;";
        Assert.Equal(request.TransmissionText, command.ExecuteScalar());
    }

    [Fact]
    public async Task FailureBetweenMessageAndAttemptRollsBackEverythingAndDoesNotNotify()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Execute("CREATE TRIGGER RejectAttempt BEFORE INSERT ON SendAttempts BEGIN SELECT RAISE(ABORT,'injected'); END;");
        var notifications = 0;
        fixture.Storage.OutgoingMessages.MessageCommitted += (_, _) => notifications++;
        await Assert.ThrowsAsync<SqliteException>(() => fixture.Storage.OutgoingMessages.PrepareAsync(fixture.Request(), CancellationToken));
        Assert.Equal(0, notifications);
        Assert.Empty(await fixture.Storage.History.GetMessagesAsync(fixture.NodeId, fixture.ConversationId, null, 10, CancellationToken));
        fixture.Execute("DROP TRIGGER RejectAttempt;");
        await fixture.Storage.OutgoingMessages.PrepareAsync(fixture.Request(), CancellationToken);
    }

    [Fact]
    public async Task RejectsWrongNodeSessionRecipientAndRetiredChannelBinding()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Storage.OutgoingMessages.PrepareAsync(request with { NodeId = Guid.NewGuid() }, CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Storage.OutgoingMessages.PrepareAsync(request with { SessionId = Guid.NewGuid() }, CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Storage.OutgoingMessages.PrepareAsync(request with { Recipient = request.Recipient with { Identity = new byte[32] } }, CancellationToken));
        var channelRequest = fixture.Request(channel: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Storage.OutgoingMessages.PrepareAsync(channelRequest with { Recipient = channelRequest.Recipient with { BindingGeneration = 999 } }, CancellationToken));
        var prepared = await fixture.Storage.OutgoingMessages.PrepareAsync(channelRequest, CancellationToken);
        fixture.Execute("UPDATE ChannelBindings SET UnboundUtc=BoundUtc;");
        Assert.Equal(prepared, await fixture.Storage.OutgoingMessages.PrepareAsync(channelRequest, CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Storage.OutgoingMessages.PrepareAsync(channelRequest with { OperationId = Guid.NewGuid() }, CancellationToken));
    }

    [Fact]
    public async Task CasRequiresAcceptedBeforeDeliveryAndProtectsTerminalAttemptsAndOwnership()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request();
        var prepared = await fixture.Storage.OutgoingMessages.PrepareAsync(request, CancellationToken);
        var transition = fixture.Transition(prepared, SendAttemptState.Prepared, SendAttemptState.Delivered);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Storage.OutgoingMessages.TransitionAsync(transition, CancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Storage.OutgoingMessages.TransitionAsync(transition with { NodeId = Guid.NewGuid() }, CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Storage.OutgoingMessages.TransitionAsync(transition with { SessionId = Guid.NewGuid() }, CancellationToken));
        await fixture.Move(prepared, SendAttemptState.Prepared, SendAttemptState.Sending);
        Assert.False(await fixture.Storage.OutgoingMessages.TransitionAsync(fixture.Transition(prepared, SendAttemptState.Prepared, SendAttemptState.Failed), CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Storage.OutgoingMessages.TransitionAsync(fixture.Transition(prepared, SendAttemptState.Sending, SendAttemptState.Accepted), CancellationToken));
        await fixture.Accept(prepared, AckExpectation.Expected);
        await fixture.Move(prepared, SendAttemptState.Accepted, SendAttemptState.Delivered);
        Assert.False(await fixture.Storage.OutgoingMessages.TransitionAsync(fixture.Transition(prepared, SendAttemptState.Accepted, SendAttemptState.Unconfirmed), CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Storage.OutgoingMessages.TransitionAsync(fixture.Transition(prepared, SendAttemptState.Delivered, SendAttemptState.Failed), CancellationToken));
        var final = Assert.Single(await fixture.Storage.OutgoingMessages.GetAttemptsAsync(fixture.NodeId, prepared.MessageId, CancellationToken));
        Assert.Equal(SendAttemptState.Delivered, final.State);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, final.ExpectedAck!.Value.ToArray());
        Assert.NotNull(final.AcceptedUtc);
        Assert.NotNull(final.CompletedUtc);
    }

    [Theory]
    [InlineData(SendAttemptState.Prepared)]
    [InlineData(SendAttemptState.Sending)]
    [InlineData(SendAttemptState.Accepted)]
    [InlineData(SendAttemptState.Delivered)]
    [InlineData(SendAttemptState.Unconfirmed)]
    [InlineData(SendAttemptState.Failed)]
    [InlineData(SendAttemptState.Unknown)]
    public async Task StartupRecoversOnlyUncertainAttemptsWithoutReplay(SendAttemptState state)
    {
        await using var fixture = await Fixture.CreateAsync();
        var prepared = await fixture.Storage.OutgoingMessages.PrepareAsync(fixture.Request(), CancellationToken);
        if (state == SendAttemptState.Failed) await fixture.Move(prepared, SendAttemptState.Prepared, state);
        else if (state != SendAttemptState.Prepared)
        {
            await fixture.Move(prepared, SendAttemptState.Prepared, SendAttemptState.Sending);
            if (state == SendAttemptState.Unknown) await fixture.Move(prepared, SendAttemptState.Sending, state);
            else if (state != SendAttemptState.Sending)
            {
                await fixture.Accept(prepared, AckExpectation.Expected);
                if (state != SendAttemptState.Accepted) await fixture.Move(prepared, SendAttemptState.Accepted, state);
            }
        }
        await fixture.Reopen();
        var final = Assert.Single(await fixture.Storage.OutgoingMessages.GetAttemptsAsync(fixture.NodeId, prepared.MessageId, CancellationToken));
        Assert.Equal(state is SendAttemptState.Sending or SendAttemptState.Accepted ? SendAttemptState.Unknown : state, final.State);
        Assert.Equal(prepared.Attempt.Id, final.Id);
        Assert.Single(await fixture.Storage.History.GetMessagesAsync(fixture.NodeId, fixture.ConversationId, null, 10, CancellationToken));
        // This fixture has no client, transport, or TX API: startup performs SQLite-only recovery.
        await fixture.Reopen();
        Assert.Equal(final, Assert.Single(await fixture.Storage.OutgoingMessages.GetAttemptsAsync(fixture.NodeId, prepared.MessageId, CancellationToken)) with { ExpectedAck = final.ExpectedAck });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotExpectedAcceptanceIsTerminalAcrossRestart(bool channel)
    {
        await using var fixture = await Fixture.CreateAsync();
        var prepared = await fixture.Storage.OutgoingMessages.PrepareAsync(fixture.Request(channel: channel), CancellationToken);
        await fixture.Move(prepared, SendAttemptState.Prepared, SendAttemptState.Sending);
        await fixture.Accept(prepared, AckExpectation.NotExpected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Storage.OutgoingMessages.TransitionAsync(fixture.Transition(prepared, SendAttemptState.Accepted, SendAttemptState.Delivered), CancellationToken));
        await fixture.Reopen();
        var final = Assert.Single(await fixture.Storage.OutgoingMessages.GetAttemptsAsync(fixture.NodeId, prepared.MessageId, CancellationToken));
        Assert.Equal(SendAttemptState.Accepted, final.State);
        Assert.Equal(AckExpectation.NotExpected, final.AckExpectation);
    }

    [Fact]
    public async Task BoundedHistoryProjectsAttemptsInBothDirectionsAndAroundAnchor()
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var i = 0; i < 12; i++) await fixture.Storage.OutgoingMessages.PrepareAsync(fixture.Request(), CancellationToken);
        var newest = await fixture.Storage.History.GetMessagesBeforeAsync(fixture.NodeId, fixture.ConversationId, null, 3, CancellationToken);
        Assert.Equal(3, newest.Items.Count);
        Assert.True(newest.HasEarlier);
        Assert.All(newest.Items, item => Assert.Equal(SendAttemptState.Prepared, item.LatestAttempt!.State));
        var oldest = await fixture.Storage.History.GetMessagesAfterAsync(fixture.NodeId, fixture.ConversationId, null, 2, CancellationToken);
        Assert.Equal(2, oldest.Items.Count);
        var around = await fixture.Storage.History.GetMessagesAroundAsync(newest.FirstPosition!, 1, 1, CancellationToken);
        Assert.Equal(3, around.Items.Count);
        Assert.All(around.Items, item => Assert.NotNull(item.LatestAttempt));
    }

    [Fact]
    public async Task UpgradeV2MakesPreMigrationBackupAndPreservesForeignKeysIndexesAndLegacyStates()
    {
        await using var fixture = await Fixture.CreateAsync();
        var privateMessage = await fixture.Storage.OutgoingMessages.PrepareAsync(fixture.Request(), CancellationToken);
        var channelMessage = await fixture.Storage.OutgoingMessages.PrepareAsync(fixture.Request(channel: true), CancellationToken);
        await fixture.Storage.DisposeAsync();
        using (var connection = fixture.Open())
        {
            // Get the exact original table definition from the supported v2 migration.
            using var schema = SqliteDatabase.CreateConnection(":memory:", SqliteOpenMode.ReadWriteCreate);
            schema.Open();
            DatabaseMigrator.ApplyPending(schema, targetVersion: 2);
            using var table = schema.CreateCommand();
            table.CommandText = "SELECT sql FROM sqlite_master WHERE name='SendAttempts';";
            var originalTableSql = (string)table.ExecuteScalar()!;
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP INDEX IX_SendAttempts_Message_StartedUtc;
                ALTER TABLE SendAttempts RENAME TO AttemptsForDowngrade;
                """;
            command.ExecuteNonQuery();
            command.CommandText = originalTableSql;
            command.ExecuteNonQuery();
            command.CommandText = """
                INSERT INTO SendAttempts
                    (Id,MessageId,SessionId,AttemptNumber,State,StartedUtc,AcceptedUtc,CompletedUtc,
                     WireTimestamp,ExpectedAck,RoundTripMilliseconds,ErrorCode)
                SELECT Id,MessageId,SessionId,AttemptNumber,1,StartedUtc,AcceptedUtc,CompletedUtc,
                       WireTimestamp,ExpectedAck,RoundTripMilliseconds,ErrorCode FROM AttemptsForDowngrade;
                DROP TABLE AttemptsForDowngrade;
                CREATE INDEX IX_SendAttempts_Message_StartedUtc ON SendAttempts(MessageId,StartedUtc);
                ALTER TABLE Messages DROP COLUMN TransmissionText;
                ALTER TABLE Contacts DROP COLUMN OutPathLength;
                ALTER TABLE Contacts DROP COLUMN RouteObservedUtc;
                DELETE FROM SchemaMigrations WHERE Version>=3;
                PRAGMA user_version=2;
                """;
            command.ExecuteNonQuery();
        }
        fixture.Storage = await LocalStorage.OpenAsync(fixture.Paths, CancellationToken);
        var backup = Assert.Single(Directory.GetFiles(fixture.Paths.BackupsDirectory, "*.db"));
        using (var connection = SqliteDatabase.CreateConnection(backup, SqliteOpenMode.ReadOnly))
        {
            connection.Open();
            Assert.Equal(2, SqliteDatabase.GetUserVersion(connection));
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT State FROM SendAttempts LIMIT 1;";
            Assert.Equal(1L, command.ExecuteScalar());
        }
        Assert.Equal(SendAttemptState.Unknown, Assert.Single(await fixture.Storage.OutgoingMessages.GetAttemptsAsync(fixture.NodeId, privateMessage.MessageId, CancellationToken)).State);
        var channel = Assert.Single(await fixture.Storage.OutgoingMessages.GetAttemptsAsync(fixture.NodeId, channelMessage.MessageId, CancellationToken));
        Assert.Equal(SendAttemptState.Accepted, channel.State);
        Assert.Equal(AckExpectation.NotExpected, channel.AckExpectation);
        using (var connection = fixture.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_key_check;";
            Assert.Null(command.ExecuteScalar());
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='IX_SendAttempts_Message_StartedUtc';";
            Assert.Equal(1L, command.ExecuteScalar());
        }
        var send = await fixture.Storage.OutgoingMessages.PrepareAsync(fixture.Request(), CancellationToken);
        Assert.True(await fixture.Move(send, SendAttemptState.Prepared, SendAttemptState.Sending));
        await fixture.Reopen();
        Assert.Single(Directory.GetFiles(fixture.Paths.BackupsDirectory, "*.db"));
    }

    [Fact]
    public async Task MigrationFailureRollsBackVersionAndColumnsAndKeepsBackup()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Storage.DisposeAsync();
        using (var connection = fixture.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE Messages DROP COLUMN TransmissionText;
                ALTER TABLE Contacts DROP COLUMN OutPathLength;
                ALTER TABLE Contacts DROP COLUMN RouteObservedUtc;
                DELETE FROM SchemaMigrations WHERE Version>=3;
                PRAGMA user_version=2;
                CREATE TABLE SendAttempts_v3 (Collision TEXT);
                """;
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<DatabaseStorageException>(() => LocalStorage.OpenAsync(fixture.Paths, CancellationToken));
        using var verify = fixture.Open();
        Assert.Equal(2, SqliteDatabase.GetUserVersion(verify));
        using var columns = verify.CreateCommand();
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Messages') WHERE name='TransmissionText';";
        Assert.Equal(0L, columns.ExecuteScalar());
        Assert.Single(Directory.GetFiles(fixture.Paths.BackupsDirectory, "*.db"));
    }

    [Fact]
    public async Task ConcurrentTransitionsAndFailedWritesPublishOnlyCommittedChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var prepared = await fixture.Storage.OutgoingMessages.PrepareAsync(fixture.Request(), CancellationToken);
        var notifications = 0;
        fixture.Storage.OutgoingMessages.MessageCommitted += (_, _) => Interlocked.Increment(ref notifications);
        fixture.Execute("CREATE TRIGGER RejectState BEFORE UPDATE ON SendAttempts BEGIN SELECT RAISE(ABORT,'injected'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => fixture.Move(prepared, SendAttemptState.Prepared, SendAttemptState.Sending));
        Assert.Equal(0, notifications);
        Assert.Equal(SendAttemptState.Prepared, Assert.Single(await fixture.Storage.OutgoingMessages.GetAttemptsAsync(fixture.NodeId, prepared.MessageId, CancellationToken)).State);
        fixture.Execute("DROP TRIGGER RejectState;");
        var results = await Task.WhenAll(
            fixture.Move(prepared, SendAttemptState.Prepared, SendAttemptState.Sending),
            fixture.Move(prepared, SendAttemptState.Prepared, SendAttemptState.Sending));
        Assert.Single(results, value => value);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task CancelledPrepareDoesNotCreateMessageOrNotification()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var notifications = 0;
        fixture.Storage.OutgoingMessages.MessageCommitted += (_, _) => notifications++;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Storage.OutgoingMessages.PrepareAsync(fixture.Request(), cancellation.Token));
        Assert.Empty(await fixture.Storage.History.GetMessagesAsync(fixture.NodeId, fixture.ConversationId, null, 10, CancellationToken));
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task PreMigrationBackupIncludesCommittedWalAndBackupFailurePreventsUpgrade()
    {
        var root = Path.Combine(Path.GetTempPath(), "MeshCore-D3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new Fixture.TestPaths(root);
        try
        {
            using var original = SqliteDatabase.CreateConnection(paths.DatabasePath, SqliteOpenMode.ReadWriteCreate);
            original.Open();
            SqliteDatabase.ConfigureWriter(original);
            DatabaseMigrator.ApplyPending(original, targetVersion: 2);
            using var insert = original.CreateCommand();
            insert.CommandText = "INSERT INTO Settings (Key,Value,UpdatedUtc) VALUES ('wal','preserve','2026-10-03T00:00:00Z');";
            insert.ExecuteNonQuery();
            var blocked = Path.Combine(root, "blocked-backups");
            await File.WriteAllTextAsync(blocked, "file", CancellationToken);
            await Assert.ThrowsAsync<IOException>(() => DatabaseWorker.OpenAsync(paths.DatabasePath, CancellationToken, blocked));
            Assert.Equal(2, SqliteDatabase.GetUserVersion(original));
            await using var storage = await LocalStorage.OpenAsync(paths, CancellationToken);
            Assert.Equal("preserve", await storage.Settings.GetAsync("wal", CancellationToken));
            var backup = Assert.Single(Directory.GetFiles(paths.BackupsDirectory, "*.db"));
            using var backupConnection = SqliteDatabase.CreateConnection(backup, SqliteOpenMode.ReadOnly);
            backupConnection.Open();
            Assert.Equal(2, SqliteDatabase.GetUserVersion(backupConnection));
            using var read = backupConnection.CreateCommand();
            read.CommandText = "SELECT Value FROM Settings WHERE Key='wal';";
            Assert.Equal("preserve", read.ExecuteScalar());
            Assert.False(File.Exists(backup + "-wal"));
            Assert.Empty(Directory.GetFiles(paths.BackupsDirectory, "*.tmp"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RestoreRecoversOutstandingAcknowledgementBeforePublishingRestoredDatabase()
    {
        await using var fixture = await Fixture.CreateAsync();
        var prepared = await fixture.Storage.OutgoingMessages.PrepareAsync(fixture.Request(), CancellationToken);
        await fixture.Move(prepared, SendAttemptState.Prepared, SendAttemptState.Sending);
        await fixture.Accept(prepared, AckExpectation.Expected);
        var backup = Path.Combine(fixture.Paths.BackupsDirectory, "manual.db");
        await fixture.Storage.BackupAsync(backup, CancellationToken);
        await fixture.Move(prepared, SendAttemptState.Accepted, SendAttemptState.Delivered);
        await fixture.Storage.RestoreAsync(backup, CancellationToken);
        var restored = Assert.Single(await fixture.Storage.OutgoingMessages.GetAttemptsAsync(fixture.NodeId, prepared.MessageId, CancellationToken));
        Assert.Equal(SendAttemptState.Unknown, restored.State);
        Assert.Equal(prepared.Attempt.Id, restored.Id);
        Assert.Equal(AckExpectation.Expected, restored.AckExpectation);
        Assert.Equal("StartupRecovery", restored.ErrorCode);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public sealed record TestPaths(string DataDirectory) : IAppPaths
        {
            public string DatabasePath => Path.Combine(DataDirectory, "messenger.db");
            public string BackupsDirectory => Path.Combine(DataDirectory, "backups");
        }
        public required TestPaths Paths { get; init; }
        public required LocalStorage Storage { get; set; }
        public Guid NodeId { get; private set; }
        public Guid SessionId { get; private set; }
        public Guid ConversationId { get; private set; }
        private Guid ChannelConversationId { get; set; }
        private ChannelBindingRecord Binding { get; set; } = null!;
        private readonly byte[] _contactKey = Enumerable.Repeat((byte)2, 32).ToArray();
        private readonly byte[] _fingerprint = Enumerable.Repeat((byte)3, 32).ToArray();
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
        public static async Task<Fixture> CreateAsync()
        {
            var paths = new TestPaths(Path.Combine(Path.GetTempPath(), "MeshCore-D3", Guid.NewGuid().ToString("N")));
            var fixture = new Fixture { Paths = paths, Storage = await LocalStorage.OpenAsync(paths, CancellationToken) };
            var profile = new ConnectionProfile
            {
                Id = Guid.NewGuid(),
                Name = "Test",
                Transport = ConnectionTransportKind.Tcp,
                TcpHost = "localhost",
                TcpPort = 5000,
                CreatedUtc = fixture._now,
                UpdatedUtc = fixture._now
            };
            await fixture.Storage.ConnectionProfiles.SaveAsync(profile, CancellationToken);
            fixture.NodeId = (await fixture.Storage.Nodes.FindOrCreateAsync(new byte[32], "Node", fixture._now, CancellationToken)).Id;
            fixture.SessionId = Guid.NewGuid();
            await fixture.Storage.Sessions.CreateAsync(new(fixture.SessionId, profile.Id, fixture.NodeId, fixture._now, null, null), CancellationToken);
            var snapshot = await fixture.Storage.Directories.ApplySnapshotAsync(fixture.NodeId, fixture.SessionId,
                [new(fixture._contactKey, "Peer", 1, 0, new byte[64], fixture._now, 0, 0)],
                [new(7, "Channel", fixture._fingerprint, ChannelAccessKind.PublicOrHashtag)], fixture._now, CancellationToken);
            fixture.Binding = Assert.Single(snapshot.ActiveBindings);
            fixture.ConversationId = (await fixture.Storage.Drafts.SaveAsync(new(fixture.NodeId, null, ConversationKind.Contact, fixture._contactKey), "draft", fixture._now, CancellationToken))!.ConversationId;
            fixture.ChannelConversationId = (await fixture.Storage.Drafts.SaveAsync(new(fixture.NodeId, null, ConversationKind.Channel, fixture._fingerprint), "draft", fixture._now, CancellationToken))!.ConversationId;
            return fixture;
        }
        public PrepareOutgoingMessage Request(bool channel = false) => new(Guid.NewGuid(), NodeId, SessionId, channel ? ChannelConversationId : ConversationId,
            channel ? new(ConversationKind.Channel, _fingerprint, Binding.Id, Binding.Slot, Binding.Generation) : new(ConversationKind.Contact, _contactKey),
            "исходный текст", "transmission", 160, _now);
        public OutgoingAttemptTransition Transition(PreparedOutgoingMessage prepared, SendAttemptState from, SendAttemptState to) =>
            new(NodeId, prepared.MessageId, prepared.Attempt.Id, SessionId, from, to, _now.AddSeconds(1));
        public Task<bool> Move(PreparedOutgoingMessage prepared, SendAttemptState from, SendAttemptState to) =>
            Storage.OutgoingMessages.TransitionAsync(Transition(prepared, from, to), CancellationToken);
        public Task<bool> Accept(PreparedOutgoingMessage prepared, AckExpectation expectation) => Storage.OutgoingMessages.TransitionAsync(
            Transition(prepared, SendAttemptState.Sending, SendAttemptState.Accepted) with { AckExpectation = expectation, ExpectedAck = expectation == AckExpectation.Expected ? new byte[] { 1, 2, 3, 4 } : null, WireTimestamp = 42 }, CancellationToken);
        public async Task Reopen() { await Storage.DisposeAsync(); Storage = await LocalStorage.OpenAsync(Paths, CancellationToken); }
        public SqliteConnection Open() { var c = SqliteDatabase.CreateConnection(Paths.DatabasePath, SqliteOpenMode.ReadWrite); c.Open(); SqliteDatabase.ConfigureReader(c); return c; }
        public void Execute(string sql) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        public async ValueTask DisposeAsync() { await Storage.DisposeAsync(); Directory.Delete(Paths.DataDirectory, true); }
    }
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;
}
