namespace MeshCoreMessenger.Desktop.Platform;

/// <summary>Pure startup policy: callers supply environment, default and working directory.</summary>
public static class DataDirectorySelection
{
    public static string Resolve(
        IReadOnlyList<string> arguments,
        string? environmentDirectory,
        string defaultDirectory,
        string workingDirectory)
    {
        string? argumentDirectory = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] != "--data-dir")
                continue;

            if (argumentDirectory is not null)
                throw new ArgumentException("Specify --data-dir only once.");
            if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]) ||
                arguments[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("--data-dir requires a folder path.");
            argumentDirectory = arguments[index];
        }

        var directory = argumentDirectory ??
            (string.IsNullOrWhiteSpace(environmentDirectory) ? defaultDirectory : environmentDirectory);
        // Path.GetFullPath is lexical; '~' is deliberately an ordinary path segment.
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory, workingDirectory));
    }

    public static string WindowTitle(string dataDirectory, string defaultDirectory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Path.TrimEndingDirectorySeparator(dataDirectory),
            Path.TrimEndingDirectorySeparator(defaultDirectory), comparison))
            return "MeshCoreMessenger";

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dataDirectory));
        return $"MeshCoreMessenger — {(name.Length == 0 ? dataDirectory : name)}";
    }
}
