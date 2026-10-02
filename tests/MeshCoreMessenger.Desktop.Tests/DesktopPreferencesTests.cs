using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Desktop.Preferences;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class DesktopPreferencesTests
{
    [Fact]
    public async Task ThemeAndGeometrySurviveReloadAfterDurableFlush()
    {
        var settings = new FakeSettingsStore();
        var first = new DesktopPreferences(settings);
        await first.LoadAsync(CancellationToken);
        var placement = new WindowPlacement(120, 80, 1280, 800, IsMaximized: true);

        first.SetTheme(DesktopThemePreference.Dark);
        first.SetWindowPlacement(placement);
        await first.FlushAsync(CancellationToken);

        var restarted = new DesktopPreferences(settings);
        await restarted.LoadAsync(CancellationToken);
        Assert.Equal(DesktopThemePreference.Dark, restarted.Snapshot.Theme);
        Assert.Equal(placement, restarted.Snapshot.WindowPlacement);
    }

    [Fact]
    public async Task ThemeAndGeometrySurviveRealSqliteRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeshCoreMessenger.Preferences.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var paths = new TestPaths(directory);
        var placement = new WindowPlacement(200, 160, 1100, 720, IsMaximized: false);
        try
        {
            await using (var storage = await LocalStorage.OpenAsync(paths, CancellationToken))
            {
                var preferences = new DesktopPreferences(storage.Settings);
                await preferences.LoadAsync(CancellationToken);
                preferences.SetTheme(DesktopThemePreference.Dark);
                preferences.SetWindowPlacement(placement);
                await preferences.FlushAsync(CancellationToken);
            }

            await using var reopened = await LocalStorage.OpenAsync(paths, CancellationToken);
            var restored = new DesktopPreferences(reopened.Settings);
            await restored.LoadAsync(CancellationToken);
            Assert.Equal(DesktopThemePreference.Dark, restored.Snapshot.Theme);
            Assert.Equal(placement, restored.Snapshot.WindowPlacement);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptedSettingsFallBackWithoutFailingStartup()
    {
        var settings = new FakeSettingsStore();
        settings.Values[DesktopPreferences.ThemeSettingKey] = "ultraviolet";
        settings.Values[DesktopPreferences.WindowPlacementSettingKey] = "{broken";
        var preferences = new DesktopPreferences(settings);

        await preferences.LoadAsync(CancellationToken);

        Assert.Equal(DesktopThemePreference.System, preferences.Snapshot.Theme);
        Assert.Null(preferences.Snapshot.WindowPlacement);
    }

    [Fact]
    public async Task WriterFailureKeepsNewestValuesForExplicitRetry()
    {
        var settings = new FakeSettingsStore();
        var preferences = new DesktopPreferences(settings);
        await preferences.LoadAsync(CancellationToken);
        preferences.SetTheme(DesktopThemePreference.Light);
        settings.Failures.Enqueue(new IOException("disk full"));

        await Assert.ThrowsAsync<DesktopPreferencesPersistenceException>(
            () => preferences.FlushAsync(CancellationToken));
        Assert.True(preferences.IsPaused);

        await preferences.RetryAsync(CancellationToken);
        Assert.False(preferences.IsPaused);
        Assert.Equal("Light", settings.Values[DesktopPreferences.ThemeSettingKey]);
    }

    [Fact]
    public void MissingMonitorCentersWindowOnPrimaryAndKeepsItAccessible()
    {
        var restored = WindowPlacementCalculator.Restore(
            new WindowPlacement(6000, 4000, 1000, 700, IsMaximized: false),
            [new ScreenArea(0, 0, 1920, 1080, IsPrimary: true)]);

        Assert.Equal(new WindowPlacement(460, 190, 1000, 700, IsMaximized: false), restored);
    }

    [Fact]
    public void InvalidBoundsAreIgnoredAndOversizedBoundsAreClamped()
    {
        var screens = new[] { new ScreenArea(0, 0, 1440, 900, IsPrimary: true) };
        Assert.Null(WindowPlacementCalculator.Restore(
            new WindowPlacement(double.NaN, 0, 1000, 700, false),
            screens));
        Assert.Null(WindowPlacementCalculator.Restore(
            new WindowPlacement(0, 0, 100, 100, false),
            screens));

        var restored = WindowPlacementCalculator.Restore(
            new WindowPlacement(-500, -300, 3000, 2000, true),
            screens);
        Assert.Equal(new WindowPlacement(0, 0, 1440, 900, true), restored);
    }

    [Fact]
    public void MaximizedCapturePreservesLastNormalBounds()
    {
        var normal = new WindowPlacement(20, 30, 1000, 700, false);

        var maximized = WindowPlacementCalculator.Capture(normal, null, isMaximized: true);
        var restoredNormal = WindowPlacementCalculator.Capture(maximized, null, isMaximized: false);

        Assert.Equal(normal with { IsMaximized = true }, maximized);
        Assert.Equal(normal, restoredNormal);
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public Queue<Exception> Failures { get; } = [];

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failures.TryDequeue(out var failure))
            {
                throw failure;
            }

            Values[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed record TestPaths(string DataDirectory) : IAppPaths
    {
        public string DatabasePath { get; } = Path.Combine(DataDirectory, "messenger.db");
        public string BackupsDirectory { get; } = Path.Combine(DataDirectory, "backups");
    }
}
