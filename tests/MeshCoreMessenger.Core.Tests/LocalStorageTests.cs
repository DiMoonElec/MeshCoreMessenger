using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class LocalStorageTests
{
    [Fact]
    public async Task CreatesNewDatabaseWithRequiredSchemaAndPragmas()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();

        await using (var storage = await LocalStorage.OpenAsync(paths, CancellationToken))
        {
            Assert.True(File.Exists(paths.DatabasePath));
        }

        using var connection = OpenReadOnly(paths.DatabasePath);
        Assert.Equal(DatabaseMigrator.CurrentVersion, ScalarInt(connection, "PRAGMA user_version;"));
        Assert.Equal("wal", ScalarString(connection, "PRAGMA journal_mode;").ToLowerInvariant());
        Assert.Equal(2, ScalarInt(connection, "PRAGMA synchronous;"));

        var tables = new HashSet<string>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
            using var result = command.ExecuteReader();
            while (result.Read())
            {
                tables.Add(result.GetString(0));
            }
        }

        string[] expectedTables =
        [
            "SchemaMigrations", "Settings", "ConnectionProfiles", "Nodes", "Sessions",
            "Contacts", "Channels", "ChannelBindings", "Conversations", "Messages",
            "SendAttempts", "Drafts",
            "PrivateTimestampFloors", "PrivateDeliveryCycles", "PrivateWireMessages",
            "ContactDeliveryHistory", "ContactDeliveryEvidence", "ContactDeliveryCandidates",
        ];
        Assert.All(expectedTables, table => Assert.Contains(table, tables));
    }

    [Fact]
    public async Task ConfiguresWriterAndEveryReaderConnection()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await using var writer = await DatabaseWorker.OpenAsync(paths.DatabasePath, CancellationToken);

        var writerPragmas = await writer.ExecuteAsync(connection => (
            ForeignKeys: ScalarInt(connection, "PRAGMA foreign_keys;"),
            BusyTimeout: ScalarInt(connection, "PRAGMA busy_timeout;"),
            JournalMode: ScalarString(connection, "PRAGMA journal_mode;"),
            Synchronous: ScalarInt(connection, "PRAGMA synchronous;")), CancellationToken);
        Assert.Equal(1, writerPragmas.ForeignKeys);
        Assert.Equal(SqliteDatabase.BusyTimeoutMilliseconds, writerPragmas.BusyTimeout);
        Assert.Equal("wal", writerPragmas.JournalMode.ToLowerInvariant());
        Assert.Equal(2, writerPragmas.Synchronous);

        var reader = new DatabaseReader(paths.DatabasePath);
        var readerPragmas = await reader.ExecuteAsync(connection => (
            ForeignKeys: ScalarInt(connection, "PRAGMA foreign_keys;"),
            BusyTimeout: ScalarInt(connection, "PRAGMA busy_timeout;")), CancellationToken);
        Assert.Equal(1, readerPragmas.ForeignKeys);
        Assert.Equal(SqliteDatabase.BusyTimeoutMilliseconds, readerPragmas.BusyTimeout);
        await Assert.ThrowsAsync<SqliteException>(() => reader.ExecuteAsync(connection =>
        {
            Execute(connection, "CREATE TABLE ReaderMustNotWrite (Id INTEGER NOT NULL);");
            return true;
        }, CancellationToken));
    }

    [Fact]
    public async Task ReopensExistingDatabaseWithoutLosingData()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();

        await using (var storage = await LocalStorage.OpenAsync(paths, CancellationToken))
        {
            await storage.Settings.SetAsync("theme", "dark", CancellationToken);
        }

        await using var reopened = await LocalStorage.OpenAsync(paths, CancellationToken);
        Assert.Equal("dark", await reopened.Settings.GetAsync("theme", CancellationToken));
    }

    [Fact]
    public async Task SavesAndRestoresSettingsIncludingUpdates()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await using var storage = await LocalStorage.OpenAsync(paths, CancellationToken);

        Assert.Null(await storage.Settings.GetAsync("selected-profile", CancellationToken));
        await storage.Settings.SetAsync("selected-profile", "first", CancellationToken);
        await storage.Settings.SetAsync("selected-profile", "second", CancellationToken);

        Assert.Equal("second", await storage.Settings.GetAsync("selected-profile", CancellationToken));
    }

    [Theory]
    [InlineData(ConnectionTransportKind.Tcp)]
    [InlineData(ConnectionTransportKind.Serial)]
    public async Task SavesAndRestoresConnectionProfile(ConnectionTransportKind transport)
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var now = DateTimeOffset.UtcNow;
        var profile = new ConnectionProfile
        {
            Id = Guid.NewGuid(),
            Name = $"Test {transport}",
            Transport = transport,
            TcpHost = transport == ConnectionTransportKind.Tcp ? "192.0.2.10" : null,
            TcpPort = transport == ConnectionTransportKind.Tcp ? 5000 : null,
            SerialPortName = transport == ConnectionTransportKind.Serial ? "/dev/cu.test" : null,
            BaudRate = transport == ConnectionTransportKind.Serial ? 115_200 : null,
            DtrEnable = true,
            RtsEnable = false,
            OpenDelayMilliseconds = 2_000,
            CommandTimeoutMilliseconds = 12_000,
            AcknowledgementTimeoutMilliseconds = 45_000,
            AutoConnect = true,
            Reconnect = true,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        await using (var storage = await LocalStorage.OpenAsync(paths, CancellationToken))
        {
            await storage.ConnectionProfiles.SaveAsync(profile, CancellationToken);
        }

        await using var reopened = await LocalStorage.OpenAsync(paths, CancellationToken);
        var restored = await reopened.ConnectionProfiles.GetAsync(profile.Id, CancellationToken);
        Assert.NotNull(restored);
        Assert.Equal(profile.Id, restored.Id);
        Assert.Equal(profile.Name, restored.Name);
        Assert.Equal(profile.Transport, restored.Transport);
        Assert.Equal(profile.TcpHost, restored.TcpHost);
        Assert.Equal(profile.TcpPort, restored.TcpPort);
        Assert.Equal(profile.SerialPortName, restored.SerialPortName);
        Assert.Equal(profile.BaudRate, restored.BaudRate);
        Assert.Equal(profile.DtrEnable, restored.DtrEnable);
        Assert.Equal(profile.RtsEnable, restored.RtsEnable);
        Assert.Equal(profile.OpenDelayMilliseconds, restored.OpenDelayMilliseconds);
        Assert.Equal(profile.CommandTimeoutMilliseconds, restored.CommandTimeoutMilliseconds);
        Assert.Equal(profile.AcknowledgementTimeoutMilliseconds, restored.AcknowledgementTimeoutMilliseconds);
        Assert.Equal(profile.AutoConnect, restored.AutoConnect);
        Assert.Equal(profile.Reconnect, restored.Reconnect);
        Assert.Equal(profile.CreatedUtc, restored.CreatedUtc);
        Assert.Equal(profile.UpdatedUtc, restored.UpdatedUtc);
    }

    [Fact]
    public async Task LegacyExpectedNodeKeyColumnIsIgnoredWithoutRequiringMigration()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var now = DateTimeOffset.UtcNow;
        var profile = new ConnectionProfile
        {
            Id = Guid.NewGuid(),
            Name = "Legacy profile",
            Transport = ConnectionTransportKind.Serial,
            SerialPortName = "/dev/cu.legacy",
            BaudRate = 115_200,
            OpenDelayMilliseconds = 0,
            CommandTimeoutMilliseconds = 1_000,
            AcknowledgementTimeoutMilliseconds = 2_000,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        await using (var storage = await LocalStorage.OpenAsync(paths, CancellationToken))
        {
            await storage.ConnectionProfiles.SaveAsync(profile, CancellationToken);
        }

        using (var legacyWriter = OpenWritable(paths.DatabasePath))
        {
            using var command = legacyWriter.CreateCommand();
            command.CommandText = "UPDATE ConnectionProfiles SET ExpectedNodePublicKey = zeroblob(32) WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        await using (var reopened = await LocalStorage.OpenAsync(paths, CancellationToken))
        {
            var restored = await reopened.ConnectionProfiles.GetAsync(profile.Id, CancellationToken);
            Assert.NotNull(restored);
            Assert.Equal("Legacy profile", restored.Name);
            await reopened.ConnectionProfiles.SaveAsync(restored with { Name = "Updated transport profile" }, CancellationToken);
        }

        using var verification = OpenReadOnly(paths.DatabasePath);
        Assert.Equal(32, ScalarInt(
            verification,
            $"SELECT length(ExpectedNodePublicKey) FROM ConnectionProfiles WHERE Id = '{profile.Id:D}';"));
        Assert.Equal(
            "Updated transport profile",
            ScalarString(verification, $"SELECT Name FROM ConnectionProfiles WHERE Id = '{profile.Id:D}';"));
    }

    [Fact]
    public async Task AppliesMigrationAndRecordsItTransactionally()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await using var storage = await LocalStorage.OpenAsync(paths, CancellationToken);

        using var connection = OpenReadOnly(paths.DatabasePath);
        Assert.Equal(DatabaseMigrator.CurrentVersion, ScalarInt(connection, "SELECT COUNT(*) FROM SchemaMigrations;"));
        Assert.Equal(DatabaseMigrator.CurrentVersion, ScalarInt(connection, "SELECT MAX(Version) FROM SchemaMigrations;"));
    }

    [Fact]
    public async Task RunningMigrationsAgainIsIdempotent()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await using (var storage = await LocalStorage.OpenAsync(paths, CancellationToken))
        {
            await storage.Settings.SetAsync("migration-marker", "preserve-me", CancellationToken);
        }

        await using (var storage = await LocalStorage.OpenAsync(paths, CancellationToken))
        {
            Assert.Equal("preserve-me", await storage.Settings.GetAsync("migration-marker", CancellationToken));
        }

        using var connection = OpenReadOnly(paths.DatabasePath);
        Assert.Equal(DatabaseMigrator.CurrentVersion, ScalarInt(connection, "SELECT COUNT(*) FROM SchemaMigrations;"));
    }

    [Fact]
    public async Task UpgradesVersionOneDatabaseWithIncomingMessageMetadataWithoutRecreatingIt()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        Directory.CreateDirectory(paths.DataDirectory);
        using (var connection = OpenWritable(paths.DatabasePath))
        {
            DatabaseMigrator.ApplyPending(connection, targetVersion: 1);
            Execute(connection, """
                INSERT INTO Nodes (Id,PublicKey,FirstSeenUtc,LastSeenUtc)
                VALUES ('node',zeroblob(32),'2026-09-25T00:00:00Z','2026-09-25T00:00:00Z');
                INSERT INTO Contacts (NodeId,PublicKey,PublicKeyPrefix,DisplayName,ContactType,Flags,PresentOnNode,UpdatedUtc)
                VALUES ('node',zeroblob(32),zeroblob(6),'Peer',1,0,1,'2026-09-25T00:00:00Z');
                INSERT INTO Conversations (Id,NodeId,Kind,ContactPublicKey,IsArchived,CreatedUtc,UpdatedUtc)
                VALUES ('conversation','node',0,zeroblob(32),0,'2026-09-25T00:00:00Z','2026-09-25T00:00:00Z');
                INSERT INTO Messages (Id,ConversationId,Direction,MessageKind,Text,ReceivedUtc)
                VALUES ('existing','conversation',0,0,'preserve','2026-09-25T00:00:00Z');
                """);
        }

        await using (var storage = await LocalStorage.OpenAsync(paths, CancellationToken))
        {
            Assert.NotNull(storage.IncomingMessages);
        }

        using var verification = OpenReadOnly(paths.DatabasePath);
        Assert.Equal(DatabaseMigrator.CurrentVersion, ScalarInt(verification, "PRAGMA user_version;"));
        Assert.Equal(DatabaseMigrator.CurrentVersion, ScalarInt(verification, "SELECT COUNT(*) FROM SchemaMigrations;"));
        Assert.Equal("preserve", ScalarString(verification, "SELECT Text FROM Messages WHERE Id = 'existing';"));
        Assert.Equal(1, ScalarInt(verification, "SELECT COUNT(*) FROM Contacts WHERE OutPathLength IS NULL AND RouteObservedUtc IS NULL;"));
        using var columns = verification.CreateCommand();
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Messages') WHERE name IN ('TextType', 'PathLength', 'BinaryDataType', 'OriginalSenderPrefix');";
        Assert.Equal(4, Convert.ToInt32(columns.ExecuteScalar()));
    }

    [Fact]
    public async Task RefusesDatabaseNewerThanApplicationWithoutChangingIt()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        Directory.CreateDirectory(paths.DataDirectory);
        using (var connection = OpenWritable(paths.DatabasePath))
        {
            Execute(connection, $"PRAGMA user_version={DatabaseMigrator.CurrentVersion + 1};");
            Execute(connection, "CREATE TABLE FutureData (Value TEXT NOT NULL);");
            Execute(connection, "INSERT INTO FutureData (Value) VALUES ('keep');");
        }

        var exception = await Assert.ThrowsAsync<DatabaseVersionTooNewException>(
            () => LocalStorage.OpenAsync(paths, CancellationToken));
        Assert.Equal(DatabaseMigrator.CurrentVersion + 1, exception.FoundVersion);

        using var verification = OpenReadOnly(paths.DatabasePath);
        Assert.Equal("keep", ScalarString(verification, "SELECT Value FROM FutureData;"));
        Assert.Equal(DatabaseMigrator.CurrentVersion + 1, ScalarInt(verification, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task FailedMigrationPreservesExistingDatabaseAndUserData()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        Directory.CreateDirectory(paths.DataDirectory);
        using (var connection = OpenWritable(paths.DatabasePath))
        {
            Execute(connection, "CREATE TABLE Settings (LegacyValue TEXT NOT NULL);");
            Execute(connection, "INSERT INTO Settings (LegacyValue) VALUES ('do-not-delete');");
        }

        var exception = await Assert.ThrowsAsync<DatabaseStorageException>(
            () => LocalStorage.OpenAsync(paths, CancellationToken));
        Assert.Contains("migration 1", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(paths.DatabasePath));

        using var verification = OpenReadOnly(paths.DatabasePath);
        Assert.Equal("do-not-delete", ScalarString(verification, "SELECT LegacyValue FROM Settings;"));
        Assert.Equal(0, ScalarInt(verification, "PRAGMA user_version;"));
        Assert.Equal(0, ScalarInt(verification,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'SchemaMigrations';"));
    }

    [Fact]
    public async Task CorruptDatabaseIsRefusedWithoutBeingDeletedOrRecreated()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        Directory.CreateDirectory(paths.DataDirectory);
        byte[] original = [0x4D, 0x43, 0x53, 0x01, 0x02, 0x03, 0x04];
        await File.WriteAllBytesAsync(paths.DatabasePath, original, CancellationToken);

        await Assert.ThrowsAsync<DatabaseIntegrityException>(
            () => LocalStorage.OpenAsync(paths, CancellationToken));

        Assert.True(File.Exists(paths.DatabasePath));
        Assert.Equal(original, await File.ReadAllBytesAsync(paths.DatabasePath, CancellationToken));
    }

    [Fact]
    public async Task SchemaEnforcesForeignKeysKeyLengthsAndUniqueness()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await using var writer = await DatabaseWorker.OpenAsync(paths.DatabasePath, CancellationToken);

        await Assert.ThrowsAsync<SqliteException>(() => writer.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Sessions (Id, ConnectionProfileId, StartedUtc)
                VALUES ('session', 'missing-profile', '2026-09-25T00:00:00Z');
                """;
            command.ExecuteNonQuery();
            return true;
        }, CancellationToken));

        await Assert.ThrowsAsync<SqliteException>(() => writer.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Nodes (Id, PublicKey, FirstSeenUtc, LastSeenUtc)
                VALUES ('bad-key', $publicKey, '2026-09-25T00:00:00Z', '2026-09-25T00:00:00Z');
                """;
            command.Parameters.Add("$publicKey", SqliteType.Blob).Value = new byte[31];
            command.ExecuteNonQuery();
            return true;
        }, CancellationToken));

        await writer.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Nodes (Id, PublicKey, FirstSeenUtc, LastSeenUtc)
                VALUES ('first', $publicKey, '2026-09-25T00:00:00Z', '2026-09-25T00:00:00Z');
                """;
            command.Parameters.Add("$publicKey", SqliteType.Blob).Value = new byte[32];
            command.ExecuteNonQuery();
            return true;
        }, CancellationToken);

        await Assert.ThrowsAsync<SqliteException>(() => writer.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Nodes (Id, PublicKey, FirstSeenUtc, LastSeenUtc)
                VALUES ('duplicate', $publicKey, '2026-09-25T00:00:00Z', '2026-09-25T00:00:00Z');
                """;
            command.Parameters.Add("$publicKey", SqliteType.Blob).Value = new byte[32];
            command.ExecuteNonQuery();
            return true;
        }, CancellationToken));
    }

    [Fact]
    public async Task BackupAndRestoreRoundTripUsesConsistentDatabaseSnapshot()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var backupPath = Path.Combine(paths.BackupsDirectory, "manual-backup.db");
        await using var storage = await LocalStorage.OpenAsync(paths, CancellationToken);
        await storage.Settings.SetAsync("backup-value", "before", CancellationToken);

        await storage.BackupAsync(backupPath, CancellationToken);
        Assert.True(File.Exists(backupPath));
        Assert.False(File.Exists(backupPath + "-wal"));

        await storage.Settings.SetAsync("backup-value", "after", CancellationToken);
        await storage.RestoreAsync(backupPath, CancellationToken);
        Assert.Equal("before", await storage.Settings.GetAsync("backup-value", CancellationToken));
    }

    [Fact]
    public async Task FailedRestoreMigrationLeavesActiveDatabaseUntouched()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var incompatibleBackup = Path.Combine(paths.BackupsDirectory, "incompatible.db");
        await using var storage = await LocalStorage.OpenAsync(paths, CancellationToken);
        await storage.Settings.SetAsync("active-value", "keep", CancellationToken);

        Directory.CreateDirectory(paths.BackupsDirectory);
        using (var connection = OpenWritable(incompatibleBackup))
        {
            Execute(connection, "CREATE TABLE Settings (LegacyValue TEXT NOT NULL);");
            Execute(connection, "INSERT INTO Settings (LegacyValue) VALUES ('legacy');");
        }

        await Assert.ThrowsAsync<DatabaseStorageException>(
            () => storage.RestoreAsync(incompatibleBackup, CancellationToken));

        Assert.Equal("keep", await storage.Settings.GetAsync("active-value", CancellationToken));
    }

    [Fact]
    public async Task WriterExecutesQueuedOperationsSequentiallyOnSingleBackgroundThread()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var callingThread = Environment.CurrentManagedThreadId;
        await using var writer = await DatabaseWorker.OpenAsync(paths.DatabasePath, CancellationToken);
        await writer.ExecuteAsync(connection =>
        {
            Execute(connection, "CREATE TABLE WriterProbe (Sequence INTEGER NOT NULL, ThreadId INTEGER NOT NULL);");
            return true;
        }, CancellationToken);

        var operations = Enumerable.Range(0, 100)
            .Select(sequence => writer.ExecuteAsync(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO WriterProbe (Sequence, ThreadId) VALUES ($sequence, $threadId);";
                command.Parameters.AddWithValue("$sequence", sequence);
                command.Parameters.AddWithValue("$threadId", Environment.CurrentManagedThreadId);
                command.ExecuteNonQuery();
                return true;
            }, CancellationToken))
            .ToArray();
        await Task.WhenAll(operations);

        using var verification = OpenReadOnly(paths.DatabasePath);
        var sequences = new List<int>();
        var threadIds = new HashSet<int>();
        using (var command = verification.CreateCommand())
        {
            command.CommandText = "SELECT Sequence, ThreadId FROM WriterProbe ORDER BY rowid;";
            using var result = command.ExecuteReader();
            while (result.Read())
            {
                sequences.Add(result.GetInt32(0));
                threadIds.Add(result.GetInt32(1));
            }
        }

        Assert.Equal(Enumerable.Range(0, 100), sequences);
        Assert.Single(threadIds);
        Assert.DoesNotContain(callingThread, threadIds);
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static SqliteConnection OpenWritable(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static int ScalarInt(SqliteConnection connection, string sql) => Convert.ToInt32(Scalar(connection, sql));
    private static string ScalarString(SqliteConnection connection, string sql) => Convert.ToString(Scalar(connection, sql))!;

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MeshCoreMessenger.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public TestAppPaths CreatePaths() => new(Path);

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
