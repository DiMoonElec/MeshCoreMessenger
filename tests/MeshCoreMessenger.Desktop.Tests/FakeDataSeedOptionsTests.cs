using MeshCoreMessenger.Desktop.DevFixtures;
using MeshCoreMessenger.Desktop.Platform;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class FakeDataSeedOptionsTests
{
    private static readonly string DefaultDirectory = Path.Combine(Path.GetTempPath(), "mc-default");
    private static readonly string TestDirectory = Path.Combine(Path.GetTempPath(), "mc-fixture");

    [Fact]
    public void NoFlagDoesNotSeedAndLargeAloneIsInert()
    {
        Assert.Null(FakeDataSeedOptions.Parse([], TestDirectory, DefaultDirectory));
        Assert.Null(FakeDataSeedOptions.Parse(["--large"], TestDirectory, DefaultDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitNonDefaultDirectoryAllowsSeed(bool large)
    {
        var args = new List<string> { "--data-dir", TestDirectory, "--seed-fake-data" };
        if (large)
            args.Add("--large");
        Assert.Equal(large, FakeDataSeedOptions.Parse(args, TestDirectory, DefaultDirectory)!.Large);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoExplicitDirectoryRefusesEvenIfEnvironmentSelectedSeparateFolder(bool environmentSpecified)
    {
        var directory = DataDirectorySelection.Resolve(["--seed-fake-data"], environmentSpecified ? TestDirectory : null,
            DefaultDirectory, Path.GetFullPath(Path.GetTempPath()));
        Assert.Throws<FakeDataSeedException>(() => FakeDataSeedOptions.Parse(["--seed-fake-data"], directory, DefaultDirectory));
    }

    [Fact]
    public void ExplicitStandardDirectoryAndLexicalAliasAreRejected()
    {
        foreach (var directory in new[] { DefaultDirectory, DefaultDirectory + Path.DirectorySeparatorChar,
            Path.Combine(DefaultDirectory, "sub", "..") })
            Assert.Throws<FakeDataSeedException>(() => FakeDataSeedOptions.Parse(
                ["--seed-fake-data", "--data-dir", directory], directory, DefaultDirectory));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Assert.Throws<FakeDataSeedException>(() => FakeDataSeedOptions.Parse(
                ["--seed-fake-data", "--data-dir", DefaultDirectory.ToUpperInvariant()], DefaultDirectory.ToUpperInvariant(), DefaultDirectory));
    }

    [Fact]
    public void SymbolicLinkAliasToStandardFolderIsRejected()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks can require privileges on Windows.
        var parent = Path.Combine(Path.GetTempPath(), "MeshCoreMessenger.SeedPolicy.Tests", Guid.NewGuid().ToString("N"));
        var standard = Path.Combine(parent, "standard");
        var alias = Path.Combine(parent, "alias");
        Directory.CreateDirectory(standard);
        try
        {
            Directory.CreateSymbolicLink(alias, standard);
            Assert.Throws<FakeDataSeedException>(() => FakeDataSeeder.ValidateDirectoryTarget(alias, standard));
        }
        finally
        {
            Directory.Delete(alias);
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void ProgramBuildGateDoesNotSeedWithoutExplicitDirectory()
    {
#if DEBUG
        Assert.Equal(5, MeshCoreMessenger.Desktop.Program.Main(["--seed-fake-data"]));
#else
        // The Release flag is ignored: an invalid explicit directory takes the normal path (code 3),
        // not the seed-policy rejection (code 5), and never reaches Avalonia.
        var invalid = Path.Combine(Path.GetTempPath(), "MeshCoreMessenger.InvalidSeedPath", "bad\0path");
        Assert.Equal(3, MeshCoreMessenger.Desktop.Program.Main(["--seed-fake-data", "--data-dir", invalid]));
#endif
    }
}
