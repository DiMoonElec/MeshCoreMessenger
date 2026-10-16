using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace MeshCoreMessenger.Core.Tests;

internal sealed class RegionTestStorage : IAsyncDisposable
{
    private static readonly string TestRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MeshCoreMessenger.Region.Tests"));
    private RegionTestStorage(IAppPaths paths, LocalStorage storage) { Paths = paths; Storage = storage; }
    internal IAppPaths Paths { get; }
    internal LocalStorage Storage { get; }
    internal static async Task<RegionTestStorage> CreateAsync(CancellationToken token, int? initialVersion = null, Action<SqliteConnection>? seed = null)
    {
        var paths = new TestPaths(Path.Combine(TestRoot, Guid.NewGuid().ToString("N")));
        if (initialVersion is { } version)
        {
            Directory.CreateDirectory(paths.DataDirectory);
            using var connection = SqliteDatabase.CreateConnection(paths.DatabasePath, SqliteOpenMode.ReadWriteCreate);
            connection.Open(); SqliteDatabase.ConfigureWriter(connection);
            DatabaseMigrator.ApplyPending(connection, version);
            seed?.Invoke(connection);
        }
        return new(paths, await LocalStorage.OpenAsync(paths, token));
    }
    public async ValueTask DisposeAsync()
    {
        await Storage.DisposeAsync();
        var target = Path.GetFullPath(Paths.DataDirectory);
        if (!string.Equals(Path.GetDirectoryName(target), TestRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its temporary storage root.");
        Directory.Delete(target, recursive: true);
    }
    private sealed record TestPaths(string DataDirectory) : IAppPaths
    {
        public string DatabasePath => Path.Combine(DataDirectory, "messenger.db");
        public string BackupsDirectory => Path.Combine(DataDirectory, "backups");
    }
}
