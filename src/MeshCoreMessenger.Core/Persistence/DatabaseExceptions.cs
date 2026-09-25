namespace MeshCoreMessenger.Core.Persistence;

public class DatabaseStorageException : Exception
{
    public DatabaseStorageException(string message) : base(message) { }
    public DatabaseStorageException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class DatabaseVersionTooNewException : DatabaseStorageException
{
    public DatabaseVersionTooNewException(int foundVersion, int supportedVersion)
        : base($"Database schema version {foundVersion} is newer than supported version {supportedVersion}. Open it with a newer MeshCoreMessenger version.")
    {
        FoundVersion = foundVersion;
        SupportedVersion = supportedVersion;
    }

    public int FoundVersion { get; }
    public int SupportedVersion { get; }
}

public sealed class DatabaseIntegrityException : DatabaseStorageException
{
    public DatabaseIntegrityException(string message) : base(message) { }
    public DatabaseIntegrityException(string message, Exception innerException) : base(message, innerException) { }
}
