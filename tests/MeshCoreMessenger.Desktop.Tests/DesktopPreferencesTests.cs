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
        first.SetCloseBehavior(DesktopCloseBehavior.ExitApplication);
        first.SetNotifyPrivateMessages(false);
        first.SetNotifyChannelMessages(false);
        first.SetWindowPlacement(placement);
        await first.FlushAsync(CancellationToken);

        var restarted = new DesktopPreferences(settings);
        await restarted.LoadAsync(CancellationToken);
        Assert.Equal(DesktopThemePreference.Dark, restarted.Snapshot.Theme);
        Assert.Equal(placement, restarted.Snapshot.WindowPlacement);
        Assert.Equal(DesktopCloseBehavior.ExitApplication, restarted.Snapshot.CloseBehavior);
        Assert.False(restarted.Snapshot.NotifyPrivateMessages);
        Assert.False(restarted.Snapshot.NotifyChannelMessages);
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
                preferences.SetCloseBehavior(DesktopCloseBehavior.ExitApplication);
                preferences.SetNotifyPrivateMessages(false);
                preferences.SetNotifyChannelMessages(false);
                await preferences.FlushAsync(CancellationToken);
            }

            await using var reopened = await LocalStorage.OpenAsync(paths, CancellationToken);
            var restored = new DesktopPreferences(reopened.Settings);
            await restored.LoadAsync(CancellationToken);
            Assert.Equal(DesktopThemePreference.Dark, restored.Snapshot.Theme);
            Assert.Equal(placement, restored.Snapshot.WindowPlacement);
            Assert.Equal(DesktopCloseBehavior.ExitApplication, restored.Snapshot.CloseBehavior);
            Assert.False(restored.Snapshot.NotifyPrivateMessages);
            Assert.False(restored.Snapshot.NotifyChannelMessages);
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
    public async Task OldDatabaseHasIndependentDefaultsAndInvalidValuesDoNotBlockStartup()
    {
        var settings = new FakeSettingsStore();
        var preferences = new DesktopPreferences(settings);
        await preferences.LoadAsync(CancellationToken);
        Assert.Equal(DesktopCloseBehavior.MinimizeToTray, preferences.Snapshot.CloseBehavior);
        Assert.True(preferences.Snapshot.NotifyPrivateMessages);
        Assert.True(preferences.Snapshot.NotifyChannelMessages);
        await preferences.FlushAsync(CancellationToken);
        Assert.Empty(settings.Values);

        settings.Values[DesktopPreferences.CloseBehaviorSettingKey] = "999";
        settings.Values[DesktopPreferences.NotifyPrivateSettingKey] = "false";
        settings.Values[DesktopPreferences.NotifyChannelsSettingKey] = "enabled";
        await preferences.LoadAsync(CancellationToken);
        Assert.Equal(DesktopCloseBehavior.MinimizeToTray, preferences.Snapshot.CloseBehavior);
        Assert.False(preferences.Snapshot.NotifyPrivateMessages);
        Assert.True(preferences.Snapshot.NotifyChannelMessages);
        Assert.Throws<ArgumentOutOfRangeException>(() => preferences.SetCloseBehavior((DesktopCloseBehavior)999));
    }

    [Fact]
    public async Task ChangeDuringBlockedFlushPersistsNewestValues()
    {
        var settings = new FakeSettingsStore();
        var preferences = new DesktopPreferences(settings);
        preferences.SetCloseBehavior(DesktopCloseBehavior.ExitApplication);
        preferences.SetNotifyPrivateMessages(false);
        preferences.SetNotifyChannelMessages(false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.BeforeWrite = async (key, token) =>
        {
            if (key != DesktopPreferences.CloseBehaviorSettingKey) return;
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var save = preferences.FlushAsync(CancellationToken);
        await started.Task.WaitAsync(CancellationToken);
        preferences.SetCloseBehavior(DesktopCloseBehavior.MinimizeToTray);
        preferences.SetNotifyPrivateMessages(true);
        preferences.SetNotifyChannelMessages(true);
        release.SetResult();
        await save;
        var restored = new DesktopPreferences(settings);
        await restored.LoadAsync(CancellationToken);
        Assert.Equal(preferences.Snapshot, restored.Snapshot);
    }

    [Fact]
    public async Task PartialFailureKeepsDirtyFieldsAndRetrySavesLatestChoice()
    {
        var settings = new FakeSettingsStore();
        var preferences = new DesktopPreferences(settings);
        preferences.SetCloseBehavior(DesktopCloseBehavior.ExitApplication);
        preferences.SetNotifyPrivateMessages(false);
        preferences.SetNotifyChannelMessages(false);
        settings.BeforeWrite = (key, _) => key == DesktopPreferences.NotifyPrivateSettingKey
            ? Task.FromException(new IOException("disk full")) : Task.CompletedTask;
        await Assert.ThrowsAsync<DesktopPreferencesPersistenceException>(() => preferences.FlushAsync(CancellationToken));
        Assert.Equal("ExitApplication", settings.Values[DesktopPreferences.CloseBehaviorSettingKey]);
        Assert.False(settings.Values.ContainsKey(DesktopPreferences.NotifyPrivateSettingKey));
        Assert.True(preferences.IsPaused);
        preferences.SetNotifyPrivateMessages(true);
        settings.BeforeWrite = null;
        await preferences.RetryAsync(CancellationToken);
        Assert.False(preferences.IsPaused);
        var restored = new DesktopPreferences(settings);
        await restored.LoadAsync(CancellationToken);
        Assert.Equal(preferences.Snapshot, restored.Snapshot);
    }

    [Fact]
    public async Task CanceledBackgroundWriteLeavesChoiceForFinalBarrier()
    {
        var settings = new FakeSettingsStore();
        var preferences = new DesktopPreferences(settings);
        preferences.SetNotifyChannelMessages(false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.BeforeWrite = async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        var save = preferences.FlushAsync(cancel.Token);
        await started.Task.WaitAsync(CancellationToken);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        Assert.False(preferences.Snapshot.NotifyChannelMessages);
        settings.BeforeWrite = null;
        await preferences.FlushAsync(CancellationToken);
        Assert.Equal("False", settings.Values[DesktopPreferences.NotifyChannelsSettingKey]);
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
        public Func<string, CancellationToken, Task>? BeforeWrite { get; set; }

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.GetValueOrDefault(key));

        public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforeWrite is { } before) await before(key, cancellationToken);
            if (Failures.TryDequeue(out var failure))
            {
                throw failure;
            }

            Values[key] = value;
        }
    }

    private sealed record TestPaths(string DataDirectory) : IAppPaths
    {
        public string DatabasePath { get; } = Path.Combine(DataDirectory, "messenger.db");
        public string BackupsDirectory { get; } = Path.Combine(DataDirectory, "backups");
    }
}
