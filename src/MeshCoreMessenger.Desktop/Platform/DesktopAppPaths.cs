using MeshCoreMessenger.Core.Application;

namespace MeshCoreMessenger.Desktop.Platform;

public sealed class DesktopAppPaths : IAppPaths
{
    private const string ApplicationDirectoryName = "MeshCoreMessenger";

    private DesktopAppPaths(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        DatabasePath = Path.Combine(DataDirectory, "messenger.db");
        BackupsDirectory = Path.Combine(DataDirectory, "Backups");
    }

    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public string BackupsDirectory { get; }

    public static DesktopAppPaths CreateDefault()
    {
        string root;
        if (OperatingSystem.IsMacOS())
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support");
        }
        else if (OperatingSystem.IsWindows())
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else
        {
            root = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("The operating system did not provide an application data directory.");

        return new DesktopAppPaths(Path.Combine(root, ApplicationDirectoryName));
    }
}
