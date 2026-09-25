namespace MeshCoreMessenger.Core.Application;

/// <summary>Application-owned filesystem locations supplied by the desktop platform layer.</summary>
public interface IAppPaths
{
    string DataDirectory { get; }
    string DatabasePath { get; }
    string BackupsDirectory { get; }
}
