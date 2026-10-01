using Microsoft.Data.Sqlite;
using SQLitePCL;

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
            using var registration = cancellationToken.UnsafeRegister(
                static state =>
                {
                    var activeConnection = (SqliteConnection)state!;
                    raw.sqlite3_interrupt(activeConnection.Handle);
                },
                connection);
            try
            {
                return action(connection);
            }
            catch (SqliteException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "The SQLite read was cancelled.",
                    exception,
                    cancellationToken);
            }
        }, cancellationToken);
    }
}
