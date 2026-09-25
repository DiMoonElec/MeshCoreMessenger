using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed class DatabaseWorker : IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly BlockingCollection<IWorkItem> _queue = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private int _disposed;

    private DatabaseWorker(string databasePath)
    {
        _databasePath = databasePath;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "MeshCoreMessenger.DatabaseWriter",
        };
        _thread.Start();
    }

    public static async Task<DatabaseWorker> OpenAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await Task.Run(() => DatabaseInspector.ValidateExisting(fullPath), cancellationToken).ConfigureAwait(false);

        var worker = new DatabaseWorker(fullPath);
        try
        {
            await worker._ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return worker;
        }
        catch
        {
            await worker.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<T> ExecuteAsync<T>(Func<SqliteConnection, T> action, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        var item = new WorkItem<T>(action, cancellationToken);
        try
        {
            _queue.Add(item, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw new ObjectDisposedException(nameof(DatabaseWorker));
        }

        return item.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        await Task.Run(_thread.Join).ConfigureAwait(false);
        _queue.Dispose();
    }

    private void Run()
    {
        SqliteConnection? connection = null;
        try
        {
            connection = SqliteDatabase.CreateConnection(_databasePath, SqliteOpenMode.ReadWriteCreate);
            connection.Open();
            SqliteDatabase.ConfigureWriter(connection);
            DatabaseMigrator.ApplyPending(connection);
            _ready.TrySetResult();

            foreach (var item in _queue.GetConsumingEnumerable())
            {
                item.Run(connection);
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            while (_queue.TryTake(out var item))
            {
                item.Fail(exception);
            }
        }
        finally
        {
            connection?.Dispose();
        }
    }

    private interface IWorkItem
    {
        void Run(SqliteConnection connection);
        void Fail(Exception exception);
    }

    private sealed class WorkItem<T>(Func<SqliteConnection, T> action, CancellationToken cancellationToken) : IWorkItem
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _completion.Task;

        public void Run(SqliteConnection connection)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                _completion.TrySetResult(action(connection));
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }
}
