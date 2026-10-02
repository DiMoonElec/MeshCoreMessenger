namespace MeshCoreMessenger.Desktop.DevFixtures;

internal sealed record FakeDataSeedOptions(bool Large)
{
    // Pure policy, also available to Release tests. Program gates activation with #if DEBUG.
    public static FakeDataSeedOptions? Parse(IReadOnlyList<string> arguments, string directory, string defaultDirectory)
    {
        if (!arguments.Contains("--seed-fake-data"))
            return null;
        if (!arguments.Contains("--data-dir"))
            throw new FakeDataSeedException("--seed-fake-data requires an explicit --data-dir; MESHCORE_DATA_DIR is not sufficient.");
        // Conservatively protect the default folder on commonly case-insensitive platforms.
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultDirectory)), comparison))
            throw new FakeDataSeedException("--seed-fake-data cannot use the standard data directory. Choose a separate test folder.");
        return new FakeDataSeedOptions(arguments.Contains("--large"));
    }
}

internal sealed class FakeDataSeedException(string message) : Exception(message);
