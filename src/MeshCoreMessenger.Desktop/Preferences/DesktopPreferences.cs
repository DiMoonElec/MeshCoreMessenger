using System.Text.Json;
using MeshCoreMessenger.Core.Persistence;

namespace MeshCoreMessenger.Desktop.Preferences;

public enum DesktopThemePreference
{
    System,
    Light,
    Dark,
}

public sealed record WindowPlacement(
    double X,
    double Y,
    double Width,
    double Height,
    bool IsMaximized);

public sealed record DesktopPreferencesSnapshot(
    DesktopThemePreference Theme,
    WindowPlacement? WindowPlacement);

internal interface IDurableDesktopPreferences
{
    bool IsPaused { get; }
    Task FlushAsync(CancellationToken cancellationToken = default);
    Task RetryAsync(CancellationToken cancellationToken = default);
}

/// <summary>Keeps desktop-only preferences dirty in memory until the shutdown barrier commits them.</summary>
public sealed class DesktopPreferences(ISettingsStore settings) : IDurableDesktopPreferences
{
    internal const string ThemeSettingKey = "desktop.theme";
    internal const string WindowPlacementSettingKey = "desktop.window-placement";

    private readonly object _gate = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private DesktopThemePreference _theme;
    private WindowPlacement? _windowPlacement;
    private long _themeRevision;
    private long _persistedThemeRevision;
    private long _placementRevision;
    private long _persistedPlacementRevision;
    private int _paused;

    public bool IsPaused => Volatile.Read(ref _paused) != 0;

    public DesktopPreferencesSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new DesktopPreferencesSnapshot(_theme, _windowPlacement);
            }
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var themeText = await settings.GetAsync(ThemeSettingKey, cancellationToken).ConfigureAwait(false);
        var placementText = await settings.GetAsync(WindowPlacementSettingKey, cancellationToken).ConfigureAwait(false);
        var theme = Enum.TryParse<DesktopThemePreference>(themeText, ignoreCase: true, out var parsedTheme) &&
            Enum.IsDefined(parsedTheme)
                ? parsedTheme
                : DesktopThemePreference.System;
        var placement = ParsePlacement(placementText);

        lock (_gate)
        {
            _theme = theme;
            _windowPlacement = placement;
            _themeRevision = _persistedThemeRevision = 0;
            _placementRevision = _persistedPlacementRevision = 0;
        }
    }

    public void SetTheme(DesktopThemePreference theme)
    {
        if (!Enum.IsDefined(theme))
        {
            throw new ArgumentOutOfRangeException(nameof(theme));
        }

        lock (_gate)
        {
            if (_theme == theme)
            {
                return;
            }

            _theme = theme;
            _themeRevision++;
        }
    }

    public void SetWindowPlacement(WindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        lock (_gate)
        {
            if (_windowPlacement == placement)
            {
                return;
            }

            _windowPlacement = placement;
            _placementRevision++;
        }
    }

    public Task RetryAsync(CancellationToken cancellationToken = default) => FlushAsync(cancellationToken);

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                DesktopPreferencesSnapshot snapshot;
                long themeRevision;
                long placementRevision;
                lock (_gate)
                {
                    snapshot = new DesktopPreferencesSnapshot(_theme, _windowPlacement);
                    themeRevision = _themeRevision;
                    placementRevision = _placementRevision;
                    if (themeRevision <= _persistedThemeRevision &&
                        placementRevision <= _persistedPlacementRevision)
                    {
                        Interlocked.Exchange(ref _paused, 0);
                        return;
                    }
                }

                try
                {
                    if (themeRevision > _persistedThemeRevision)
                    {
                        await settings.SetAsync(
                            ThemeSettingKey,
                            snapshot.Theme.ToString(),
                            cancellationToken).ConfigureAwait(false);
                        lock (_gate)
                        {
                            _persistedThemeRevision = Math.Max(_persistedThemeRevision, themeRevision);
                        }
                    }

                    if (placementRevision > _persistedPlacementRevision && snapshot.WindowPlacement is not null)
                    {
                        await settings.SetAsync(
                            WindowPlacementSettingKey,
                            JsonSerializer.Serialize(snapshot.WindowPlacement),
                            cancellationToken).ConfigureAwait(false);
                        lock (_gate)
                        {
                            _persistedPlacementRevision = Math.Max(
                                _persistedPlacementRevision,
                                placementRevision);
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Interlocked.Exchange(ref _paused, 1);
                    throw new DesktopPreferencesPersistenceException(exception);
                }
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private static WindowPlacement? ParsePlacement(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var placement = JsonSerializer.Deserialize<WindowPlacement>(value);
            return placement is not null &&
                IsFinite(placement.X) &&
                IsFinite(placement.Y) &&
                IsFinite(placement.Width) &&
                IsFinite(placement.Height)
                    ? placement
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

internal sealed class DesktopPreferencesPersistenceException(Exception innerException)
    : Exception("Desktop preferences could not be persisted.", innerException);
