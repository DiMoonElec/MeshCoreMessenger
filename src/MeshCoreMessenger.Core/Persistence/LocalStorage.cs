using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Persistence.Sqlite;

namespace MeshCoreMessenger.Core.Persistence;

/// <summary>Owns the local SQLite database and its serialized write queue.</summary>
public sealed class LocalStorage : IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseWorker _writer;
    private int _disposed;

    private LocalStorage(string databasePath, DatabaseWorker writer)
    {
        _databasePath = databasePath;
        _writer = writer;
        var reader = new DatabaseReader(databasePath);
        Settings = new SqliteSettingsStore(writer, reader);
        ConnectionProfiles = new SqliteConnectionProfileStore(writer, reader);
        Nodes = new SqliteNodeStore(writer, reader);
        Sessions = new SqliteSessionStore(writer, reader);
        Directories = new SqliteDirectoryStore(writer, reader);
        IncomingMessages = new SqliteIncomingMessageStore(writer);
        History = new SqliteLocalHistoryReader(reader);
        ReadStates = new SqliteConversationReadStateStore(writer, reader);
        Drafts = new SqliteDraftStore(writer, reader);
        ConversationDirectory = new SqliteConversationDirectoryReader(reader);
    }

    public ISettingsStore Settings { get; }
    public IConnectionProfileStore ConnectionProfiles { get; }
    public INodeStore Nodes { get; }
    public ISessionStore Sessions { get; }
    public IDirectoryStore Directories { get; }
    public IIncomingMessageStore IncomingMessages { get; }
    public ILocalHistoryReader History { get; }
    public IConversationReadStateStore ReadStates { get; }
    public IDraftStore Drafts { get; }
    public IConversationDirectoryReader ConversationDirectory { get; }

    public static async Task<LocalStorage> OpenAsync(IAppPaths paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var databasePath = Path.GetFullPath(paths.DatabasePath);
        try
        {
            Directory.CreateDirectory(Path.GetFullPath(paths.DataDirectory));
            Directory.CreateDirectory(Path.GetFullPath(paths.BackupsDirectory));
            var writer = await DatabaseWorker.OpenAsync(databasePath, cancellationToken).ConfigureAwait(false);
            return new LocalStorage(databasePath, writer);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DatabaseStorageException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new DatabaseStorageException(
                $"Could not open local SQLite database '{databasePath}'. " +
                "The database was not deleted or automatically recreated.",
                exception);
        }
    }

    public async Task BackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var destination = Path.GetFullPath(destinationPath);
        if (PathsReferToSameFile(_databasePath, destination))
        {
            throw new ArgumentException("Backup destination must differ from the active database.", nameof(destinationPath));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await _writer.ExecuteAsync(connection =>
            {
                using var backup = SqliteDatabase.CreateConnection(temporary, SqliteOpenMode.ReadWriteCreate);
                backup.Open();
                connection.BackupDatabase(backup);
                return true;
            }, cancellationToken).ConfigureAwait(false);

            DatabaseInspector.ValidateExisting(temporary);
            DeleteSidecars(destination);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            DeleteDatabaseFiles(temporary);
        }
    }

    public async Task RestoreAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var sourcePathFull = Path.GetFullPath(sourcePath);
        if (PathsReferToSameFile(_databasePath, sourcePathFull))
        {
            throw new ArgumentException("Restore source must differ from the active database.", nameof(sourcePath));
        }

        if (!File.Exists(sourcePathFull))
        {
            throw new FileNotFoundException("SQLite backup was not found.", sourcePathFull);
        }

        await Task.Run(() => DatabaseInspector.ValidateExisting(sourcePathFull), cancellationToken).ConfigureAwait(false);

        var stagingPath = Path.Combine(
            Path.GetDirectoryName(_databasePath)!,
            $".restore-{Guid.NewGuid():N}.db");
        try
        {
            await Task.Run(() =>
            {
                using var source = SqliteDatabase.CreateConnection(sourcePathFull, SqliteOpenMode.ReadOnly);
                source.Open();
                SqliteDatabase.ConfigureReader(source);
                using var staging = SqliteDatabase.CreateConnection(stagingPath, SqliteOpenMode.ReadWriteCreate);
                staging.Open();
                source.BackupDatabase(staging);
            }, cancellationToken).ConfigureAwait(false);

            await using (var stagingWorker = await DatabaseWorker.OpenAsync(stagingPath, cancellationToken).ConfigureAwait(false))
            {
                // Opening applies any supported pending migration to the disposable staging copy.
            }
            DatabaseInspector.ValidateExisting(stagingPath);

            await _writer.ExecuteAsync(connection =>
            {
                using var source = SqliteDatabase.CreateConnection(stagingPath, SqliteOpenMode.ReadOnly);
                source.Open();
                SqliteDatabase.ConfigureReader(source);
                source.BackupDatabase(connection);
                SqliteDatabase.ConfigureWriter(connection);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteDatabaseFiles(stagingPath);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        return _writer.DisposeAsync();
    }

    private static bool PathsReferToSameFile(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void DeleteDatabaseFiles(string databasePath)
    {
        DeleteFileIfPresent(databasePath);
        DeleteSidecars(databasePath);
    }

    private static void DeleteSidecars(string databasePath)
    {
        DeleteFileIfPresent(databasePath + "-wal");
        DeleteFileIfPresent(databasePath + "-shm");
    }

    private static void DeleteFileIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
