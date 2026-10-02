using MeshCoreMessenger.Desktop.Platform;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class DataDirectorySelectionTests
{
    private static readonly string WorkingDirectory = Path.GetFullPath(Path.GetTempPath());
    private static readonly string DefaultDirectory = Path.Combine(WorkingDirectory, "default-account");

    [Theory]
    [InlineData("argument", "environment", "argument")]
    [InlineData(null, "environment", "environment")]
    [InlineData(null, null, "default-account")]
    [InlineData(null, "", "default-account")]
    [InlineData(null, "  ", "default-account")]
    public void SelectionUsesArgumentThenEnvironmentThenDefault(string? argument, string? environment, string expected)
    {
        string[] arguments = argument is null ? [] : ["--data-dir", argument];
        Assert.Equal(Path.Combine(WorkingDirectory, expected),
            DataDirectorySelection.Resolve(arguments, environment, DefaultDirectory, WorkingDirectory));
    }

    [Theory]
    [InlineData("account/../other/", "other")]
    [InlineData("./account", "account")]
    [InlineData("~/account", "~/account")]
    public void NormalizationIsLexicalAndDoesNotExpandTilde(string supplied, string expected)
    {
        Assert.Equal(Path.GetFullPath(expected, WorkingDirectory),
            DataDirectorySelection.Resolve(["--data-dir", supplied], null, DefaultDirectory, WorkingDirectory));
    }

    [Fact]
    public void AbsolutePathAndUnrelatedArgumentsAreSupported()
    {
        Assert.Equal(DefaultDirectory, DataDirectorySelection.Resolve(
            ["--other", "value", "--data-dir", DefaultDirectory], "ignored", DefaultDirectory, WorkingDirectory));
    }

    [Theory]
    [InlineData("--data-dir")]
    [InlineData("--data-dir", "")]
    [InlineData("--data-dir", "--other")]
    [InlineData("--data-dir", "one", "--data-dir", "two")]
    public void MalformedArgumentsAreRejected(params string[] arguments)
    {
        Assert.Throws<ArgumentException>(() =>
            DataDirectorySelection.Resolve(arguments, null, DefaultDirectory, WorkingDirectory));
        Assert.Equal(3, MeshCoreMessenger.Desktop.Program.Main(arguments));
    }

    [Fact]
    public void PublicFactoryNormalizesPathsWithoutCreatingDirectory()
    {
        var directory = Path.Combine(WorkingDirectory, Guid.NewGuid().ToString("N"), "..", Guid.NewGuid().ToString("N"));
        var paths = DesktopAppPaths.CreateForDirectory(directory);
        Assert.Equal(Path.GetFullPath(directory), paths.DataDirectory);
        Assert.Equal(Path.Combine(paths.DataDirectory, "messenger.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(paths.DataDirectory, "Backups"), paths.BackupsDirectory);
        Assert.False(Directory.Exists(paths.DataDirectory));
    }

    [Fact]
    public void TitleOnlyLabelsNonDefaultDirectory()
    {
        Assert.Equal("MeshCoreMessenger", DataDirectorySelection.WindowTitle(DefaultDirectory, DefaultDirectory));
        Assert.Equal("MeshCoreMessenger", DataDirectorySelection.WindowTitle(DefaultDirectory + Path.DirectorySeparatorChar, DefaultDirectory));
        Assert.Equal("MeshCoreMessenger — second-account", DataDirectorySelection.WindowTitle(
            Path.Combine(WorkingDirectory, "second-account"), DefaultDirectory));
    }
}
