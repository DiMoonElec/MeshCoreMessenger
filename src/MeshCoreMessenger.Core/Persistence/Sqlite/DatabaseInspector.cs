using Microsoft.Data.Sqlite;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal static class DatabaseInspector
{
    public static void ValidateExisting(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return;
        }

        try
        {
            using var connection = SqliteDatabase.CreateConnection(databasePath, SqliteOpenMode.ReadOnly);
            connection.Open();
            SqliteDatabase.ConfigureReader(connection);

            using (var integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA quick_check;";
                var result = Convert.ToString(integrity.ExecuteScalar());
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new DatabaseIntegrityException($"SQLite integrity check failed: {result ?? "no result"}.");
                }
            }

            var version = SqliteDatabase.GetUserVersion(connection);
            if (version > DatabaseMigrator.CurrentVersion)
            {
                throw new DatabaseVersionTooNewException(version, DatabaseMigrator.CurrentVersion);
            }

            if (version > 0)
            {
                using var history = connection.CreateCommand();
                history.CommandText = "SELECT MAX(Version) FROM SchemaMigrations;";
                var recorded = Convert.ToInt32(history.ExecuteScalar());
                if (recorded != version)
                {
                    throw new DatabaseIntegrityException(
                        $"Database version metadata is inconsistent: user_version={version}, last migration={recorded}.");
                }
            }
        }
        catch (DatabaseStorageException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new DatabaseIntegrityException($"Could not safely open SQLite database '{databasePath}'.", exception);
        }
    }
}
