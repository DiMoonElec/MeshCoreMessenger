using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class RegionMigrationTests
{
    private static SqliteConnection Open(int version = 7)
    {
        var connection = SqliteDatabase.CreateConnection(":memory:", SqliteOpenMode.ReadWriteCreate);
        connection.Open(); SqliteDatabase.ConfigureReader(connection);
        DatabaseMigrator.ApplyPending(connection, version); return connection;
    }
    private static void Execute(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static long Number(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
    private const string Seed = """
        INSERT INTO ConnectionProfiles(Id,Name,Transport,TcpHost,TcpPort,DtrEnable,RtsEnable,OpenDelayMilliseconds,
            CommandTimeoutMilliseconds,AcknowledgementTimeoutMilliseconds,AutoConnect,Reconnect,CreatedUtc,UpdatedUtc)
            VALUES('tcp','TCP',0,'localhost',5000,0,0,0,1000,1000,0,0,'2026-10-16','2026-10-16');
        INSERT INTO ConnectionProfiles(Id,Name,Transport,BleDeviceId,DtrEnable,RtsEnable,OpenDelayMilliseconds,
            CommandTimeoutMilliseconds,AcknowledgementTimeoutMilliseconds,AutoConnect,Reconnect,CreatedUtc,UpdatedUtc)
            VALUES('ble','BLE',2,'fixture',0,0,0,1000,1000,0,0,'2026-10-16','2026-10-16');
        INSERT INTO Nodes VALUES('node-a',zeroblob(32),'A','2026-10-16','2026-10-16'),
            ('node-b',CAST('12345678901234567890123456789012' AS BLOB),'B','2026-10-16','2026-10-16');
        INSERT INTO Sessions VALUES('session-a','tcp','node-a','2026-10-16',NULL,NULL),
            ('session-b','ble','node-a','2026-10-16',NULL,NULL),('session-c','tcp','node-b','2026-10-16',NULL,NULL);
        INSERT INTO Channels VALUES('channel-a','node-a','Channel',zeroblob(32),0,'2026-10-16','2026-10-16'),
            ('channel-b','node-b','Channel',zeroblob(32),0,'2026-10-16','2026-10-16');
        INSERT INTO Settings VALUES('region-marker','keep','2026-10-16');
        """;
    private const string Region = """
        INSERT INTO Regions VALUES('region-a','node-a',0,'A','Label',CAST('1234567890123456' AS BLOB),zeroblob(32),0,'2026-10-16','2026-10-16',0);
        """;

    [Fact]
    public void UpgradeCreatesInheritedPreferencesWithoutTouchingExistingDirectoriesOrProfiles()
    {
        using var connection = Open(); Execute(connection, Seed); DatabaseMigrator.ApplyPending(connection);
        Assert.Equal(DatabaseMigrator.CurrentVersion, Number(connection, "PRAGMA user_version"));
        Assert.Equal(2, Number(connection, "SELECT COUNT(*) FROM ChannelRegionPreferences WHERE Mode=0 AND RegionId IS NULL AND Revision=0"));
        Assert.Equal(2, Number(connection, "SELECT COUNT(*) FROM ConnectionProfiles"));
        Assert.Equal(3, Number(connection, "SELECT COUNT(*) FROM Sessions"));
        Assert.Equal(2, Number(connection, "SELECT COUNT(*) FROM Channels"));
        Assert.Equal(0, Number(connection, "SELECT COUNT(*) FROM Regions"));
        Assert.Equal(0, Number(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
        Assert.Equal(4, Number(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type='index' AND name IN('UX_Sessions_Id_Node','IX_Regions_Node_Archived','IX_ChannelRegionPreferences_Region','IX_RegionObservations_Node_LastSeen')"));
        DatabaseMigrator.ApplyPending(connection); Assert.Equal(DatabaseMigrator.CurrentVersion, Number(connection, "SELECT COUNT(*) FROM SchemaMigrations"));
    }

    [Fact]
    public void ForeignKeysModeChecksKeysAndByteLimitsRejectInvalidCrossNodeData()
    {
        using var connection = Open(); Execute(connection, Seed); DatabaseMigrator.ApplyPending(connection); Execute(connection, Region);
        foreach (var update in new[] { "Mode=1", "Mode=0,RegionId='region-a'", "Mode=3", "Revision=-1" })
            Assert.Throws<SqliteException>(() => Execute(connection, $"UPDATE ChannelRegionPreferences SET {update} WHERE ChannelId='channel-a'"));
        Assert.Throws<SqliteException>(() => Execute(connection, "UPDATE ChannelRegionPreferences SET Mode=1,RegionId='region-a' WHERE ChannelId='channel-b'"));
        Execute(connection, "UPDATE ChannelRegionPreferences SET Mode=1,RegionId='region-a' WHERE ChannelId='channel-a'");
        Assert.Throws<SqliteException>(() => Execute(connection, "DELETE FROM Regions WHERE Id='region-a'"));
        foreach (var update in new[] { "Key=zeroblob(16)", "Key=zeroblob(15)", "KeyFingerprint=zeroblob(16)", "Kind=5", "Archived=2", "ProtocolName='*'", "ProtocolName='a,b'", "ProtocolName='a'||char(0)||'b'", "ProtocolName='яяяяяяяяяяяяяяяя'" })
            Assert.Throws<SqliteException>(() => Execute(connection, $"UPDATE Regions SET {update} WHERE Id='region-a'"));
        Execute(connection, "UPDATE Regions SET ProtocolName='яяяяяяяяяяяяяяя' WHERE Id='region-a'");
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO NodeRegionSnapshots VALUES('node-a','session-c',1,'2026-10-16',2,1,NULL,NULL)"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO NodeRegionSnapshots VALUES('node-a',NULL,NULL,NULL,2,1,NULL,NULL)"));
        Execute(connection, "INSERT INTO NodeRegionSnapshots VALUES('node-a','session-a',1,'2026-10-16',2,1,NULL,NULL)");
        Execute(connection, "INSERT INTO RegionObservations VALUES('node-a',zeroblob(32),'$private',0,'2026-10-16','2026-10-16')");
        Assert.Throws<SqliteException>(() => Execute(connection, "UPDATE RegionObservations SET RepeaterPublicKey=zeroblob(8)"));
        Assert.Equal(0, Number(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
    }

    [Fact]
    public void InjectedFailureRollsBackAllNewTablesIndexesPreferencesAndVersion()
    {
        using var connection = Open(); Execute(connection, Seed);
        Execute(connection, "CREATE TRIGGER FailRegionMigration BEFORE INSERT ON SchemaMigrations WHEN NEW.Version=8 BEGIN SELECT RAISE(ABORT,'injected REGION migration failure'); END;");
        Assert.Throws<DatabaseStorageException>(() => DatabaseMigrator.ApplyPending(connection));
        Assert.Equal(7, Number(connection, "PRAGMA user_version"));
        Assert.Equal(7, Number(connection, "SELECT COUNT(*) FROM SchemaMigrations"));
        Assert.Equal(0, Number(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name IN('Regions','ChannelRegionPreferences','NodeRegionSnapshots','RegionObservations','UX_Sessions_Id_Node')"));
        Assert.Equal(2, Number(connection, "SELECT COUNT(*) FROM Channels"));
        Execute(connection, "DROP TRIGGER FailRegionMigration"); DatabaseMigrator.ApplyPending(connection);
        Assert.Equal(DatabaseMigrator.CurrentVersion, Number(connection, "PRAGMA user_version"));
    }

    [Fact]
    public void EmptyAndVersionOneDatabasesUpgradeToSameSchema()
    {
        foreach (var version in new[] { 0, 1 })
        {
            using var connection = Open(version); DatabaseMigrator.ApplyPending(connection);
            Assert.Equal(DatabaseMigrator.CurrentVersion, Number(connection, "PRAGMA user_version"));
            Assert.Equal(0, Number(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
        }
    }

    [Fact]
    public async Task StartupBacksUpVersionSevenAndReopenPreservesProfilesAndInheritedPolicies()
    {
        await using var fixture = await RegionTestStorage.CreateAsync(TestContext.Current.CancellationToken, 7, connection => Execute(connection, Seed));
        var backup = Assert.Single(Directory.GetFiles(fixture.Paths.BackupsDirectory, "*.db"));
        using (var old = SqliteDatabase.CreateConnection(backup, SqliteOpenMode.ReadOnly))
        {
            old.Open(); Assert.Equal(7, Number(old, "PRAGMA user_version"));
            Assert.Equal(2, Number(old, "SELECT COUNT(*) FROM Channels"));
        }
        await fixture.Storage.DisposeAsync();
        await using (var reopened = await LocalStorage.OpenAsync(fixture.Paths, TestContext.Current.CancellationToken))
            Assert.Equal("keep", await reopened.Settings.GetAsync("region-marker", TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(fixture.Paths.BackupsDirectory, "*.db"));
        using var current = SqliteDatabase.CreateConnection(fixture.Paths.DatabasePath, SqliteOpenMode.ReadOnly);
        current.Open(); Assert.Equal(DatabaseMigrator.CurrentVersion, Number(current, "PRAGMA user_version"));
        Assert.Equal(2, Number(current, "SELECT COUNT(*) FROM ChannelRegionPreferences WHERE Mode=0 AND RegionId IS NULL"));
    }

    [Fact]
    public void ForeignKeyCheckRejectsPreexistingCorruptionAndRollsBackNewObjects()
    {
        using var connection = Open(); Execute(connection, Seed);
        Execute(connection, "PRAGMA foreign_keys=OFF; UPDATE Channels SET NodeId='missing' WHERE Id='channel-a'; PRAGMA foreign_keys=ON;");
        Assert.Throws<DatabaseStorageException>(() => DatabaseMigrator.ApplyPending(connection));
        Assert.Equal(7, Number(connection, "PRAGMA user_version"));
        Assert.Equal(0, Number(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name='Regions'"));
        Assert.Equal(1, Number(connection, "PRAGMA foreign_keys"));
    }
}
