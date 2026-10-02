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
        Assert.Equal(2, MeshCoreMessenger.Desktop.Program.Main(["--data-dir", paths.DataDirectory]));
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
        Assert.Throws<ApplicationInstanceAlreadyRunningException>(() => ApplicationInstanceLock.Acquire(firstPaths));
        Assert.Throws<ApplicationInstanceAlreadyRunningException>(() => ApplicationInstanceLock.Acquire(secondPaths));
    }

    [Fact]
    public void EquivalentNormalizedDirectoryIsRejected()
    {
        using var temporary = new TemporaryDirectory();
        using var first = ApplicationInstanceLock.Acquire(temporary.CreatePaths("shared"));
        var equivalent = DesktopAppPaths.CreateForDirectory(Path.Combine(temporary.Path, "unused", "..", "shared"));
        Assert.Throws<ApplicationInstanceAlreadyRunningException>(() => ApplicationInstanceLock.Acquire(equivalent));
    }

    [Fact]
    public void FileInsteadOfDirectoryUsesDataDirectoryExitCode()
    {
        using var temporary = new TemporaryDirectory();
        var file = Path.Combine(temporary.Path, "file");
        File.WriteAllText(file, "preserved");
        Assert.Throws<ApplicationDataDirectoryUnavailableException>(() =>
            ApplicationInstanceLock.Acquire(DesktopAppPaths.CreateForDirectory(file)));
        Assert.Equal(3, MeshCoreMessenger.Desktop.Program.Main(["--data-dir", file]));
        Assert.Equal("preserved", File.ReadAllText(file));
    }

    [Fact]
    public void UnusableLockPathIsDirectoryErrorNotAlreadyRunning()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths("blocked");
        Directory.CreateDirectory(Path.Combine(paths.DataDirectory, ".meshcoremessenger.lock"));
        Assert.Throws<ApplicationDataDirectoryUnavailableException>(() => ApplicationInstanceLock.Acquire(paths));
        Assert.Equal(3, MeshCoreMessenger.Desktop.Program.Main(["--data-dir", paths.DataDirectory]));
    }

    [Fact]
    public void NonWritableDirectoryIsRejectedEvenWithExistingLockFile()
    {
        if (OperatingSystem.IsWindows())
            return; // Unix directory permissions; the unusable-lock-path test also runs on Windows.
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths("read-only");
        using (ApplicationInstanceLock.Acquire(paths)) { }
        var originalMode = File.GetUnixFileMode(paths.DataDirectory);
        try
        {
            File.SetUnixFileMode(paths.DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.Throws<ApplicationDataDirectoryUnavailableException>(() => ApplicationInstanceLock.Acquire(paths));
            Assert.Equal(3, MeshCoreMessenger.Desktop.Program.Main(["--data-dir", paths.DataDirectory]));
        }
        finally
        {
            File.SetUnixFileMode(paths.DataDirectory, originalMode);
        }
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

        public DesktopAppPaths CreatePaths(string name) => DesktopAppPaths.CreateForDirectory(System.IO.Path.Combine(Path, name));

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

}
