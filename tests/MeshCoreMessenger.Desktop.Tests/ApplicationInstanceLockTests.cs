using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Desktop.Platform;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class ApplicationInstanceLockTests
{
    [Fact]
    public void FirstInstanceAcquiresLock()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths("first");

        using var instanceLock = ApplicationInstanceLock.Acquire(paths);

        Assert.Equal(Path.GetFullPath(paths.DataDirectory), instanceLock.DataDirectory);
        Assert.True(File.Exists(instanceLock.LockFilePath));
    }

    [Fact]
    public void SecondInstanceForSameDirectoryIsRejected()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths("shared");
        using var first = ApplicationInstanceLock.Acquire(paths);

        var exception = Assert.Throws<ApplicationInstanceAlreadyRunningException>(
            () => ApplicationInstanceLock.Acquire(paths));

        Assert.Equal(first.DataDirectory, exception.DataDirectory);
        Assert.Contains("already using", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LockCanBeAcquiredAgainAfterReleaseDespitePersistentLockFile()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths("restart");
        string lockFilePath;
        using (var first = ApplicationInstanceLock.Acquire(paths))
        {
            lockFilePath = first.LockFilePath;
        }

        Assert.True(File.Exists(lockFilePath));
        using var restarted = ApplicationInstanceLock.Acquire(paths);
        Assert.Equal(lockFilePath, restarted.LockFilePath);
    }

    [Fact]
    public void DifferentDataDirectoriesDoNotConflict()
    {
        using var temporary = new TemporaryDirectory();
        var firstPaths = temporary.CreatePaths("one");
        var secondPaths = temporary.CreatePaths("two");

        using var first = ApplicationInstanceLock.Acquire(firstPaths);
        using var second = ApplicationInstanceLock.Acquire(secondPaths);

        Assert.NotEqual(first.LockFilePath, second.LockFilePath);
    }

    [Fact]
    public void InvalidDataDirectoryProducesClearError()
    {
        using var temporary = new TemporaryDirectory();
        var regularFile = Path.Combine(temporary.Path, "not-a-directory");
        File.WriteAllText(regularFile, "content");
        var invalidDirectory = Path.Combine(regularFile, "data");

        var exception = Assert.Throws<ApplicationDataDirectoryUnavailableException>(
            () => ApplicationInstanceLock.Acquire(invalidDirectory));

        Assert.Equal(invalidDirectory, exception.DataDirectory);
        Assert.Contains("cannot use data directory", exception.Message, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "MeshCoreMessenger.InstanceLock.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public TestAppPaths CreatePaths(string name) => new(System.IO.Path.Combine(Path, name));

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
        public string BackupsDirectory { get; } = System.IO.Path.Combine(DataDirectory, "Backups");
    }
}
