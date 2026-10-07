using System.Text.Json;
using MeshCoreMessenger.Core.Persistence;

namespace MeshCoreMessenger.Desktop.Preferences;

public enum DesktopThemePreference
{
    System,
    Light,
    Dark,
}

public enum DesktopCloseBehavior
{
    MinimizeToTray,
    ExitApplication,
}

public enum DesktopStartupWindowMode
{
    LastState,
    Minimized,
    Tray,
}

public enum DesktopWindowPresentation
{
    Open,
    Minimized,
    Tray,
}

public sealed record WindowPlacement(
    double X,
    double Y,
    double Width,
    double Height,
    bool IsMaximized);

public sealed record DesktopPreferencesSnapshot(
    DesktopThemePreference Theme,
    WindowPlacement? WindowPlacement,
    DesktopCloseBehavior CloseBehavior = DesktopCloseBehavior.MinimizeToTray,
    bool NotifyPrivateMessages = true,
    bool NotifyChannelMessages = true,
    DesktopStartupWindowMode StartupWindowMode = DesktopStartupWindowMode.LastState,
    DesktopWindowPresentation LastWindowPresentation = DesktopWindowPresentation.Open);

internal interface IDurableDesktopPreferences
{
    bool IsPaused { get; }
    Task FlushAsync(CancellationToken cancellationToken = default);
    Task RetryAsync(CancellationToken cancellationToken = default);
}

/// <summary>Keeps desktop-only preferences dirty until a background save or the shutdown barrier commits them.</summary>
public sealed class DesktopPreferences(ISettingsStore settings) : IDurableDesktopPreferences
{
    internal const string ThemeSettingKey = "desktop.theme";
    internal const string WindowPlacementSettingKey = "desktop.window-placement";
    internal const string CloseBehaviorSettingKey = "desktop.close-behavior";
    internal const string NotifyPrivateSettingKey = "desktop.notifications.private";
    internal const string NotifyChannelsSettingKey = "desktop.notifications.channels";

    internal const string StartupWindowModeSettingKey = "desktop.startup-window-mode";
    internal const string LastWindowPresentationSettingKey = "desktop.last-window-presentation";

    private DesktopStartupWindowMode _startupWindowMode;
    private long _startupWindowModeRevision, _persistedStartupWindowModeRevision;
    private DesktopWindowPresentation _lastWindowPresentation;
    private long _lastWindowPresentationRevision, _persistedLastWindowPresentationRevision;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private DesktopThemePreference _theme;
    private WindowPlacement? _windowPlacement;
    private DesktopCloseBehavior _closeBehavior;
    private bool _notifyPrivateMessages = true;
    private bool _notifyChannelMessages = true;
    private long _themeRevision;
    private long _persistedThemeRevision;
    private long _placementRevision;
    private long _persistedPlacementRevision;
    private long _closeRevision;
    private long _persistedCloseRevision;
    private long _privateRevision;
    private long _persistedPrivateRevision;
    private long _channelsRevision;
    private long _persistedChannelsRevision;
    private int _paused;

    public bool IsPaused => Volatile.Read(ref _paused) != 0;

    public DesktopPreferencesSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return CreateSnapshot();
            }
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var themeText = await settings.GetAsync(ThemeSettingKey, cancellationToken).ConfigureAwait(false);
        var placementText = await settings.GetAsync(WindowPlacementSettingKey, cancellationToken).ConfigureAwait(false);
        var closeText = await settings.GetAsync(CloseBehaviorSettingKey, cancellationToken).ConfigureAwait(false);
        var privateText = await settings.GetAsync(NotifyPrivateSettingKey, cancellationToken).ConfigureAwait(false);
        var channelsText = await settings.GetAsync(NotifyChannelsSettingKey, cancellationToken).ConfigureAwait(false);
        var startupWindowModeText = await settings.GetAsync(StartupWindowModeSettingKey, cancellationToken).ConfigureAwait(false);
        var lastWindowPresentationText = await settings.GetAsync(LastWindowPresentationSettingKey, cancellationToken).ConfigureAwait(false);
        var theme = Enum.TryParse<DesktopThemePreference>(themeText, ignoreCase: true, out var parsedTheme) &&
            Enum.IsDefined(parsedTheme)
                ? parsedTheme
                : DesktopThemePreference.System;
        var placement = ParsePlacement(placementText);

        lock (_gate)
        {
            _startupWindowMode = Enum.TryParse<DesktopStartupWindowMode>(startupWindowModeText, true, out var startupWindowMode) && Enum.IsDefined(startupWindowMode) ? startupWindowMode : DesktopStartupWindowMode.LastState;
            _startupWindowModeRevision = _persistedStartupWindowModeRevision = 0;
            _lastWindowPresentation = Enum.TryParse<DesktopWindowPresentation>(lastWindowPresentationText, true, out var lastWindowPresentation) && Enum.IsDefined(lastWindowPresentation) ? lastWindowPresentation : DesktopWindowPresentation.Open;
            _lastWindowPresentationRevision = _persistedLastWindowPresentationRevision = 0;
            _theme = theme;
            _windowPlacement = placement;
            _closeBehavior = Enum.TryParse<DesktopCloseBehavior>(closeText, true, out var close) && Enum.IsDefined(close)
                ? close : DesktopCloseBehavior.MinimizeToTray;
            _notifyPrivateMessages = !bool.TryParse(privateText, out var notifyPrivate) || notifyPrivate;
            _notifyChannelMessages = !bool.TryParse(channelsText, out var notifyChannels) || notifyChannels;
            _themeRevision = _persistedThemeRevision = 0;
            _placementRevision = _persistedPlacementRevision = 0;
            _closeRevision = _persistedCloseRevision = 0;
            _privateRevision = _persistedPrivateRevision = 0;
            _channelsRevision = _persistedChannelsRevision = 0;
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

    public void SetStartupWindowMode(DesktopStartupWindowMode value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        lock (_gate)
        {
            if (_startupWindowMode == value) return;
            _startupWindowMode = value;
            _startupWindowModeRevision++;
        }
    }

    public void SetLastWindowPresentation(DesktopWindowPresentation value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        lock (_gate)
        {
            if (_lastWindowPresentation == value) return;
            _lastWindowPresentation = value;
            _lastWindowPresentationRevision++;
        }
    }

    public void SetCloseBehavior(DesktopCloseBehavior behavior)
    {
        if (!Enum.IsDefined(behavior)) throw new ArgumentOutOfRangeException(nameof(behavior));
        lock (_gate)
        {
            if (_closeBehavior == behavior) return;
            _closeBehavior = behavior;
            _closeRevision++;
        }
    }

    public void SetNotifyPrivateMessages(bool value)
    {
        lock (_gate)
        {
            if (_notifyPrivateMessages == value) return;
            _notifyPrivateMessages = value;
            _privateRevision++;
        }
    }

    public void SetNotifyChannelMessages(bool value)
    {
        lock (_gate)
        {
            if (_notifyChannelMessages == value) return;
            _notifyChannelMessages = value;
            _channelsRevision++;
        }
    }

    // Called with _gate held, so every flush captures one consistent in-memory snapshot.
    private DesktopPreferencesSnapshot CreateSnapshot() =>
        new(_theme, _windowPlacement, _closeBehavior, _notifyPrivateMessages, _notifyChannelMessages, _startupWindowMode, _lastWindowPresentation);

    public Task RetryAsync(CancellationToken cancellationToken = default) => FlushAsync(cancellationToken);

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                DesktopPreferencesSnapshot snapshot;
                long startupWindowModeRevision;
                long lastWindowPresentationRevision;
                long themeRevision;
                long placementRevision;
                long closeRevision;
                long privateRevision;
                long channelsRevision;
                lock (_gate)
                {
                    snapshot = CreateSnapshot();
                    startupWindowModeRevision = _startupWindowModeRevision;
                    lastWindowPresentationRevision = _lastWindowPresentationRevision;
                    themeRevision = _themeRevision;
                    placementRevision = _placementRevision;
                    closeRevision = _closeRevision;
                    privateRevision = _privateRevision;
                    channelsRevision = _channelsRevision;
                    if (startupWindowModeRevision <= _persistedStartupWindowModeRevision &&
                        lastWindowPresentationRevision <= _persistedLastWindowPresentationRevision &&
                        themeRevision <= _persistedThemeRevision &&
                        placementRevision <= _persistedPlacementRevision &&
                        closeRevision <= _persistedCloseRevision &&
                        privateRevision <= _persistedPrivateRevision &&
                        channelsRevision <= _persistedChannelsRevision)
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

                    if (startupWindowModeRevision > _persistedStartupWindowModeRevision)
                    {
                        await settings.SetAsync(StartupWindowModeSettingKey, snapshot.StartupWindowMode.ToString(), cancellationToken).ConfigureAwait(false);
                        lock (_gate) _persistedStartupWindowModeRevision = startupWindowModeRevision;
                    }
                    if (lastWindowPresentationRevision > _persistedLastWindowPresentationRevision)
                    {
                        await settings.SetAsync(LastWindowPresentationSettingKey, snapshot.LastWindowPresentation.ToString(), cancellationToken).ConfigureAwait(false);
                        lock (_gate) _persistedLastWindowPresentationRevision = lastWindowPresentationRevision;
                    }
                    if (closeRevision > _persistedCloseRevision)
                    {
                        await settings.SetAsync(CloseBehaviorSettingKey, snapshot.CloseBehavior.ToString(), cancellationToken).ConfigureAwait(false);
                        lock (_gate) _persistedCloseRevision = closeRevision;
                    }
                    if (privateRevision > _persistedPrivateRevision)
                    {
                        await settings.SetAsync(NotifyPrivateSettingKey, snapshot.NotifyPrivateMessages.ToString(), cancellationToken).ConfigureAwait(false);
                        lock (_gate) _persistedPrivateRevision = privateRevision;
                    }
                    if (channelsRevision > _persistedChannelsRevision)
                    {
                        await settings.SetAsync(NotifyChannelsSettingKey, snapshot.NotifyChannelMessages.ToString(), cancellationToken).ConfigureAwait(false);
                        lock (_gate) _persistedChannelsRevision = channelsRevision;
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
