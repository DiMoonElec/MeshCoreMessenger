using MeshCoreMessenger.Core.Application;

namespace MeshCoreMessenger.Desktop.Platform;

/// <summary>Holds an operating-system file lock for one application data directory.</summary>
public sealed class ApplicationInstanceLock : IDisposable
{
    private const string LockFileName = ".meshcoremessenger.lock";
    private FileStream? _handle;

    private ApplicationInstanceLock(string dataDirectory, string lockFilePath, FileStream handle)
    {
        DataDirectory = dataDirectory;
        LockFilePath = lockFilePath;
        _handle = handle;
    }

    public string DataDirectory { get; }
    public string LockFilePath { get; }

    public static ApplicationInstanceLock Acquire(IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Acquire(paths.DataDirectory);
    }

    public static ApplicationInstanceLock Acquire(string dataDirectory)
    {
        string fullDataDirectory;
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
            fullDataDirectory = Path.GetFullPath(dataDirectory);
            Directory.CreateDirectory(fullDataDirectory);
            // Even an existing writable lock file must not hide a read-only data directory.
            using var probe = new FileStream(
                Path.Combine(fullDataDirectory, $".write-probe-{Guid.NewGuid():N}"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            probe.WriteByte(0);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ApplicationDataDirectoryUnavailableException(dataDirectory, exception);
        }

        var lockFilePath = Path.Combine(fullDataDirectory, LockFileName);
        try
        {
            var handle = new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return new ApplicationInstanceLock(fullDataDirectory, lockFilePath, handle);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ApplicationDataDirectoryUnavailableException(fullDataDirectory, exception);
        }
        catch (IOException exception)
        {
            if (Directory.Exists(lockFilePath))
                throw new ApplicationDataDirectoryUnavailableException(fullDataDirectory, exception);
            throw new ApplicationInstanceAlreadyRunningException(fullDataDirectory, exception);
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
}

public abstract class ApplicationInstanceLockException : Exception
{
    protected ApplicationInstanceLockException(string message, string dataDirectory, Exception innerException)
        : base(message, innerException)
    {
        DataDirectory = dataDirectory;
    }

    public string DataDirectory { get; }
}

public sealed class ApplicationInstanceAlreadyRunningException : ApplicationInstanceLockException
{
    internal ApplicationInstanceAlreadyRunningException(string dataDirectory, Exception innerException)
        : base(
            $"Another MeshCoreMessenger instance is already using data directory '{dataDirectory}'.",
            dataDirectory,
            innerException)
    {
    }
}

public sealed class ApplicationDataDirectoryUnavailableException : ApplicationInstanceLockException
{
    internal ApplicationDataDirectoryUnavailableException(string? dataDirectory, Exception innerException)
        : base(
            $"MeshCoreMessenger cannot use data directory '{dataDirectory ?? "<null>"}': {innerException.Message}",
            dataDirectory ?? string.Empty,
            innerException)
    {
    }
}
