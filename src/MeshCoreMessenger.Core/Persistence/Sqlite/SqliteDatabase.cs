using Microsoft.Data.Sqlite;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal static class SqliteDatabase
{
    internal const int BusyTimeoutMilliseconds = 5_000;

    public static SqliteConnection CreateConnection(string databasePath, SqliteOpenMode mode)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = BusyTimeoutMilliseconds / 1_000,
        }.ToString();

        return new SqliteConnection(connectionString);
    }

    public static void ConfigureWriter(SqliteConnection connection)
    {
        ExecutePragma(connection, "PRAGMA foreign_keys=ON;");
        ExecutePragma(connection, $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};");

        using var journalMode = connection.CreateCommand();
        journalMode.CommandText = "PRAGMA journal_mode=WAL;";
        var mode = Convert.ToString(journalMode.ExecuteScalar());
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new DatabaseStorageException($"SQLite refused WAL mode and selected '{mode ?? "unknown"}'.");
        }

        ExecutePragma(connection, "PRAGMA synchronous=FULL;");
    }

    public static void ConfigureReader(SqliteConnection connection)
    {
        ExecutePragma(connection, "PRAGMA foreign_keys=ON;");
        ExecutePragma(connection, $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};");
    }

    public static int GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void ExecutePragma(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
