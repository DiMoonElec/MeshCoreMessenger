using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class BleProfileMigrationTests
{
    [Fact]
    public void UpgradePreservesProfilesReferencesSettingsAndSchemaObjects()
    {
        using var connection = OpenVersionSix();
        Seed(connection);
        var profiles = Rows(connection, "SELECT * FROM ConnectionProfiles ORDER BY Id;");
        var sessions = Rows(connection, "SELECT * FROM Sessions;");
        var nodes = Rows(connection, "SELECT * FROM Nodes;");
        var settings = Rows(connection, "SELECT * FROM Settings;");
        Execute(connection, """
            CREATE INDEX IX_TestProfileName ON ConnectionProfiles(Name);
            CREATE TRIGGER TestProfileUpdate AFTER UPDATE ON ConnectionProfiles BEGIN
                UPDATE Settings SET Value='trigger fired' WHERE Key='marker';
            END;
            """);

        DatabaseMigrator.ApplyPending(connection);

        Assert.Equal(7, Number(connection, "PRAGMA user_version;"));
        Assert.Equal(1, Number(connection, "PRAGMA foreign_keys;"));
        Assert.Empty(Rows(connection, "PRAGMA foreign_key_check;"));
        Assert.Equal(profiles, Rows(connection, """
            SELECT Id,Name,Transport,TcpHost,TcpPort,SerialPortName,BaudRate,DtrEnable,RtsEnable,
                OpenDelayMilliseconds,CommandTimeoutMilliseconds,AcknowledgementTimeoutMilliseconds,
                AutoConnect,Reconnect,ExpectedNodePublicKey,CreatedUtc,UpdatedUtc
            FROM ConnectionProfiles ORDER BY Id;
            """));
        Assert.Equal(sessions, Rows(connection, "SELECT * FROM Sessions;"));
        Assert.Equal(nodes, Rows(connection, "SELECT * FROM Nodes;"));
        Assert.Equal(settings, Rows(connection, "SELECT * FROM Settings;"));
        Assert.Equal(2, Number(connection, "SELECT COUNT(*) FROM ConnectionProfiles WHERE BleDeviceId IS NULL AND BleDeviceName IS NULL;"));
        Assert.Equal(1, Number(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name='IX_TestProfileName';"));
        Execute(connection, "UPDATE ConnectionProfiles SET Name='Updated' WHERE Id='tcp';");
        Assert.Equal("trigger fired", Rows(connection, "SELECT Value FROM Settings WHERE Key='marker';")[0][0]);
        Assert.Throws<SqliteException>(() => Execute(connection, "DELETE FROM ConnectionProfiles WHERE Id='tcp';"));
        DatabaseMigrator.ApplyPending(connection);
        Assert.Equal(7, Number(connection, "SELECT COUNT(*) FROM SchemaMigrations;"));
    }

    [Theory]
    [InlineData("BleDeviceId=NULL")]
    [InlineData("BleDeviceId=''")]
    [InlineData("BleDeviceId=' ' || char(9,10,160)")]
    [InlineData("TcpHost='host'")]
    [InlineData("TcpPort=5000")]
    [InlineData("SerialPortName='COM1'")]
    [InlineData("BaudRate=115200")]
    [InlineData("Transport=3")]
    [InlineData("CommandTimeoutMilliseconds=0")]
    public void BleRowRejectsInvalidSettings(string update)
    {
        using var connection = OpenVersionSix();
        DatabaseMigrator.ApplyPending(connection);
        InsertBle(connection);
        Assert.Throws<SqliteException>(() => Execute(connection, $"UPDATE ConnectionProfiles SET {update} WHERE Id='ble';"));
    }

    [Theory]
    [InlineData("tcp", "BleDeviceId='extra'")]
    [InlineData("tcp", "BleDeviceName='extra'")]
    [InlineData("serial", "BleDeviceId='extra'")]
    [InlineData("serial", "BleDeviceName='extra'")]
    public void ExistingTransportsRejectBleFields(string id, string update)
    {
        using var connection = OpenVersionSix();
        Seed(connection);
        DatabaseMigrator.ApplyPending(connection);
        Assert.Throws<SqliteException>(() => Execute(connection, $"UPDATE ConnectionProfiles SET {update} WHERE Id='{id}';"));
    }

    [Fact]
    public void FailureAfterRebuildRollsBackDataSchemaHistoryAndForeignKeys()
    {
        using var connection = OpenVersionSix();
        Seed(connection);
        Execute(connection, """
            CREATE TRIGGER FailBleMigration BEFORE INSERT ON SchemaMigrations WHEN NEW.Version=7 BEGIN
                SELECT RAISE(ABORT,'injected migration failure');
            END;
            """);
        var profiles = Rows(connection, "SELECT * FROM ConnectionProfiles ORDER BY Id;");
        var sessions = Rows(connection, "SELECT * FROM Sessions;");

        Assert.Throws<DatabaseStorageException>(() => DatabaseMigrator.ApplyPending(connection));

        Assert.Equal(6, Number(connection, "PRAGMA user_version;"));
        Assert.Equal(6, Number(connection, "SELECT COUNT(*) FROM SchemaMigrations;"));
        Assert.Equal(1, Number(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(profiles, Rows(connection, "SELECT * FROM ConnectionProfiles ORDER BY Id;"));
        Assert.Equal(sessions, Rows(connection, "SELECT * FROM Sessions;"));
        Assert.Equal(0, Number(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name='ConnectionProfiles_Ble';"));
        Execute(connection, "DROP TRIGGER FailBleMigration;");
        DatabaseMigrator.ApplyPending(connection);
        InsertBle(connection);
    }

    [Fact]
    public void ForeignKeyViolationAbortsRebuildAndRestoresEnforcement()
    {
        using var connection = OpenVersionSix();
        Seed(connection);
        Execute(connection, "PRAGMA foreign_keys=OFF; UPDATE Sessions SET ConnectionProfileId='missing'; PRAGMA foreign_keys=ON;");
        Assert.Throws<DatabaseStorageException>(() => DatabaseMigrator.ApplyPending(connection));
        Assert.Equal(6, Number(connection, "PRAGMA user_version;"));
        Assert.Equal(1, Number(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(17, Number(connection, "SELECT COUNT(*) FROM pragma_table_info('ConnectionProfiles');"));
    }

    [Fact]
    public async Task StartupBacksUpVersionSixAndReopensVersionSeven()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeshCoreMessenger.BleMigration.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var paths = new TestPaths(directory);
        try
        {
            using (var old = SqliteDatabase.CreateConnection(paths.DatabasePath, SqliteOpenMode.ReadWriteCreate))
            {
                old.Open();
                SqliteDatabase.ConfigureWriter(old);
                DatabaseMigrator.ApplyPending(old, 6);
                Seed(old);
            }
            await using (var storage = await LocalStorage.OpenAsync(paths, TestContext.Current.CancellationToken))
                Assert.Equal("keep", await storage.Settings.GetAsync("marker", TestContext.Current.CancellationToken));
            var backups = Directory.GetFiles(paths.BackupsDirectory, "*.db");
            Assert.Single(backups);
            using (var backup = SqliteDatabase.CreateConnection(backups[0], SqliteOpenMode.ReadOnly))
            {
                backup.Open();
                Assert.Equal(6, Number(backup, "PRAGMA user_version;"));
                Assert.Equal(2, Number(backup, "SELECT COUNT(*) FROM ConnectionProfiles;"));
            }
            await using (var reopened = await LocalStorage.OpenAsync(paths, TestContext.Current.CancellationToken))
                Assert.Equal("keep", await reopened.Settings.GetAsync("marker", TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(paths.BackupsDirectory, "*.db"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static SqliteConnection OpenVersionSix()
    {
        var connection = SqliteDatabase.CreateConnection(":memory:", SqliteOpenMode.ReadWriteCreate);
        connection.Open();
        SqliteDatabase.ConfigureReader(connection);
        DatabaseMigrator.ApplyPending(connection, 6);
        return connection;
    }

    private static void Seed(SqliteConnection connection) => Execute(connection, """
        INSERT INTO ConnectionProfiles VALUES
            ('tcp','TCP',0,'localhost',5000,NULL,NULL,1,0,25,1234,5678,1,0,zeroblob(32),'2026-09-25','2026-10-01'),
            ('serial','Serial',1,NULL,NULL,'COM8',230400,0,1,750,9876,5432,0,1,NULL,'2026-09-26','2026-10-02');
        INSERT INTO Settings VALUES ('marker','keep','2026-09-25');
        INSERT INTO Nodes VALUES ('node',zeroblob(32),'Node','2026-09-25','2026-10-01');
        INSERT INTO Sessions VALUES ('session','tcp','node','2026-09-25','2026-10-01','closed');
        """);

    private static void InsertBle(SqliteConnection connection) => Execute(connection, """
        INSERT INTO ConnectionProfiles (Id,Name,Transport,BleDeviceId,BleDeviceName,DtrEnable,RtsEnable,
            OpenDelayMilliseconds,CommandTimeoutMilliseconds,AcknowledgementTimeoutMilliseconds,
            AutoConnect,Reconnect,CreatedUtc,UpdatedUtc)
        VALUES ('ble','BLE',2,'offline-device',NULL,1,1,2000,10000,30000,0,1,'2026-10-16','2026-10-16');
        """);

    private static int Number(SqliteConnection connection, string sql) => Convert.ToInt32(Rows(connection, sql)[0][0]);
    private static List<object[]> Rows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object[]>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(values);
        }
        return rows;
    }
    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private sealed record TestPaths(string DataDirectory) : IAppPaths
    {
        public string DatabasePath => Path.Combine(DataDirectory, "messenger.db");
        public string BackupsDirectory => Path.Combine(DataDirectory, "backups");
    }
}
