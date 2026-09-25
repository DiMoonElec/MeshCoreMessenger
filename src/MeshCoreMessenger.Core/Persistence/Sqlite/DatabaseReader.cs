using Microsoft.Data.Sqlite;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed class DatabaseReader(string databasePath)
{
    private readonly string _databasePath = Path.GetFullPath(databasePath);

    public Task<T> ExecuteAsync<T>(Func<SqliteConnection, T> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = SqliteDatabase.CreateConnection(_databasePath, SqliteOpenMode.ReadOnly);
            connection.Open();
            SqliteDatabase.ConfigureReader(connection);
            return action(connection);
        }, cancellationToken);
    }
}
