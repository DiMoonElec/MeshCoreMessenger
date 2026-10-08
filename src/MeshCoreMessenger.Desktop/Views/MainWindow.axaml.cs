using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using System.Reflection;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Preferences;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreMessenger.Desktop.Views.Chat;
using MeshCoreMessenger.Desktop.Views.Connection;
using MeshCoreMessenger.Desktop.Views.Settings;
using MeshCoreMessenger.Desktop.Views.Devices;

namespace MeshCoreMessenger.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public string BuildVersionTitleSuffix { get; } = GetBuildVersionTitleSuffix();

    private static string GetBuildVersionTitleSuffix()
    {
        var version = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var separator = version.IndexOf('+');
        if (separator < 0) return $" — v{version} (git unknown)";
        var revision = version[(separator + 1)..];
        return $" — v{version[..separator]} + {revision[..Math.Min(8, revision.Length)]}";
    }

    public static readonly StyledProperty<string> DataDirectoryTitleSuffixProperty =
        AvaloniaProperty.Register<MainWindow, string>(nameof(DataDirectoryTitleSuffix), string.Empty);

    public string DataDirectoryTitleSuffix
    {
        get => GetValue(DataDirectoryTitleSuffixProperty);
        set => SetValue(DataDirectoryTitleSuffixProperty, value);
    }

    private readonly IDesktopShutdownCoordinator? _shutdown;
    private bool _shutdownAccepted;
    private Func<bool>? _canHideToTray;
    private Action? _exitApplication;
    private bool _shutdownRequestActive;
    private bool _placementInitialized;
    private bool _closed;
    private DesktopWindowPresentation? _startupPresentation;
    private bool _startupPresentationApplied;
    private bool _applyingStartupPresentation;
    private WindowState _activationRestoreState = WindowState.Normal;
    private WindowPlacement? _normalPlacement;
    private MainWindowViewModel? _subscribedViewModel;
    private readonly ChatsView _publicChats = new();
    private readonly ChatsView _privateChats = new();
    private readonly ConnectionSettingsView _connection = new();
    private readonly ApplicationSettingsView _settings = new();
    private readonly DevicesWorkspaceView _devices = new();

    public MainWindow()
    {
        InitializeComponent();
        ShellHost.RegisterContent(ShellSection.PublicChats, _publicChats, () => _subscribedViewModel?.IsChatWorkspaceVisible == true);
        ShellHost.RegisterContent(ShellSection.PrivateChats, _privateChats, () => _subscribedViewModel?.IsChatWorkspaceVisible == true);
        ShellHost.RegisterContent(ShellSection.Connection, _connection);
        ShellHost.RegisterContent(ShellSection.Settings, _settings);
        ShellHost.RegisterContent(ShellSection.Devices, _devices);
        DataContextChanged += OnWindowDataContextChanged;
        KeyDown += OnWorkspaceKeyDown;
        SizeChanged += (_, _) => CaptureWindowPlacement();
        PositionChanged += (_, _) => CaptureWindowPlacement();
        PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.Property == WindowStateProperty)
            {
                if (WindowState != WindowState.Minimized) _activationRestoreState = WindowState;
                CaptureWindowPlacement();
                if (!_applyingStartupPresentation && WindowState == WindowState.Minimized && CanActivateExistingWindow && _canHideToTray?.Invoke() == true) Hide();
            }
            if (eventArgs.Property == WindowStateProperty || eventArgs.Property == IsVisibleProperty)
                CaptureWindowPresentation();
        };
        Opened += (_, _) => { ApplySavedWindowPlacement(); ApplyStartupPresentation(); };
    }

    public MainWindow(IDesktopShutdownCoordinator shutdown)
        : this()
    {
        _shutdown = shutdown;
    }

    protected override void OnClosing(WindowClosingEventArgs eventArgs)
    {
        CaptureWindowPlacement();
        base.OnClosing(eventArgs);
        if (_shutdown is null ||
            _shutdownAccepted ||
            eventArgs.CloseReason == WindowCloseReason.OSShutdown)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (ShouldHideOnClose(eventArgs.CloseReason,
            _subscribedViewModel?.SelectedCloseBehavior.Value ?? DesktopCloseBehavior.MinimizeToTray,
            _canHideToTray?.Invoke() == true) && CanActivateExistingWindow)
        {
            Hide();
            return;
        }
        RequestExit();
    }

    internal static bool ShouldHideOnClose(WindowCloseReason reason, DesktopCloseBehavior behavior, bool trayAvailable) =>
        reason == WindowCloseReason.WindowClosing && behavior == DesktopCloseBehavior.MinimizeToTray && trayAvailable;

    internal void ConfigureTrayLifecycle(Func<bool> canHideToTray, Action exitApplication)
    {
        _canHideToTray = canHideToTray;
        _exitApplication = exitApplication;
    }

    internal void ConfigureStartupPresentation()
    {
        if (DataContext is MainWindowViewModel root)
            _startupPresentation = ResolveStartupPresentation(root.SelectedStartupWindowMode.Value, root.LastWindowPresentation);
    }

    internal static DesktopWindowPresentation ResolveStartupPresentation(DesktopStartupWindowMode mode, DesktopWindowPresentation last) => mode switch
    {
        DesktopStartupWindowMode.Minimized => DesktopWindowPresentation.Minimized,
        DesktopStartupWindowMode.Tray => DesktopWindowPresentation.Tray,
        _ => last,
    };

    private void ApplyStartupPresentation()
    {
        if (_startupPresentationApplied || _startupPresentation is not { } presentation) return;
        _applyingStartupPresentation = true;
        try
        {
            if (presentation == DesktopWindowPresentation.Tray && _canHideToTray?.Invoke() == true) Hide();
            else if (presentation != DesktopWindowPresentation.Open) WindowState = WindowState.Minimized;
        }
        finally { _applyingStartupPresentation = false; _startupPresentationApplied = true; }
        CaptureWindowPresentation();
    }

    private void CaptureWindowPresentation()
    {
        if (!_startupPresentationApplied || _applyingStartupPresentation || !CanActivateExistingWindow || DataContext is not MainWindowViewModel root) return;
        root.UpdateWindowPresentation(!IsVisible ? DesktopWindowPresentation.Tray : WindowState == WindowState.Minimized
            ? DesktopWindowPresentation.Minimized : DesktopWindowPresentation.Open);
    }

    internal void RequestExit()
    {
        VerifyAccess();
        if (_closed || _shutdownAccepted || _shutdown is null) return;
        CaptureWindowPlacement();
        CaptureWindowPresentation();
        if (_shutdownRequestActive)
        {
            return;
        }

        _shutdownRequestActive = true;
        _ = CompleteShutdownAndCloseAsync();
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        _closed = true;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        }

        base.OnClosed(eventArgs);
    }

    internal bool CanActivateExistingWindow => !_closed && !_shutdownAccepted && !_shutdownRequestActive;

    internal bool TryActivateExistingWindow()
    {
        VerifyAccess();
        if (!CanActivateExistingWindow) return false;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = _activationRestoreState;
        Activate();
        return true;
    }

    private void OnWindowDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _subscribedViewModel = DataContext as MainWindowViewModel;
        _publicChats.DataContext = _subscribedViewModel?.Chats.Public;
        _privateChats.DataContext = _subscribedViewModel?.Chats.Private;
        _connection.DataContext = _subscribedViewModel?.Profiles;
        _settings.DataContext = _subscribedViewModel;
        _devices.DataContext = _subscribedViewModel?.Devices;
        ShellHost.RefreshContent();
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ApplyTheme(_subscribedViewModel.SelectedTheme.Value);
        }
    }

    // Collection refresh null selections are not user requests to close a conversation.
    internal static ConversationListItem? ConversationSelectionToApply(object? selectedItem) =>
        selectedItem as ConversationListItem;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsChatWorkspaceVisible)) ShellHost.RefreshContent();
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.SelectedTheme) &&
            sender is MainWindowViewModel viewModel)
        {
            ApplyTheme(viewModel.SelectedTheme.Value);
        }
    }

    private static void ApplyTheme(DesktopThemePreference theme)
    {
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = App.ToThemeVariant(theme);
        }
    }

    private void OnWorkspaceKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Handled || DataContext is not MainWindowViewModel owner) return;
        if (owner.Modal.IsOpen) return;
        if (owner.IsChatWorkspaceVisible)
        {
            (owner.Shell.SelectedItem.Section == ShellSection.PublicChats ? _publicChats : _privateChats).HandleShortcut(args);
            return;
        }
        if (owner.Shell.SelectedItem.Section != ShellSection.Settings) return;
        var action = DesktopShortcutRouter.Route(new DesktopShortcutContext(args.Key, args.KeyModifiers,
            FocusManager?.GetFocusedElement() is TextBox, false, false, true, false, false, false));
        if (action != DesktopShortcutAction.CloseSettings) return;
        owner.Shell.ReturnFromSettingsCommand.Execute(null);
        args.Handled = true;
    }

    private void ApplySavedWindowPlacement()
    {
        if (_placementInitialized) return;
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var screenAreas = Screens.All
            .Select(screen => new ScreenArea(
                screen.WorkingArea.X,
                screen.WorkingArea.Y,
                screen.WorkingArea.Width,
                screen.WorkingArea.Height,
                ReferenceEquals(screen, Screens.Primary)))
            .ToArray();
        var restored = WindowPlacementCalculator.Restore(viewModel.SavedWindowPlacement, screenAreas);
        if (restored is not null)
        {
            Position = new PixelPoint((int)Math.Round(restored.X), (int)Math.Round(restored.Y));
            var targetScreen = Screens.ScreenFromPoint(new PixelPoint(
                (int)Math.Round(restored.X + (restored.Width / 2)),
                (int)Math.Round(restored.Y + (restored.Height / 2)))) ?? Screens.Primary;
            var scale = targetScreen?.Scaling is > 0 ? targetScreen.Scaling : 1;
            Width = restored.Width / scale;
            Height = restored.Height / scale;
            _normalPlacement = restored with { IsMaximized = false };
            if (restored.IsMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        }

        _placementInitialized = true;
        CaptureWindowPlacement();
    }

    private void CaptureWindowPlacement()
    {
        if (!_placementInitialized || _closed || _shutdownAccepted || WindowState == WindowState.Minimized || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        WindowPlacement? currentNormal = null;
        if (WindowState == WindowState.Normal)
        {
            // Placement uses screen coordinates. On macOS Retina, screen scaling is 1
            // while RenderScaling is 2; backing pixels must not inflate the saved window.
            var scale = Screens.ScreenFromWindow(this)?.Scaling is > 0 and var screenScale ? screenScale : 1;
            currentNormal = new WindowPlacement(
                Position.X,
                Position.Y,
                Bounds.Width * scale,
                Bounds.Height * scale,
                IsMaximized: false);
        }

        var captured = WindowPlacementCalculator.Capture(
            _normalPlacement,
            currentNormal,
            WindowState == WindowState.Maximized);
        if (captured is not null)
        {
            _normalPlacement = captured with { IsMaximized = false };
            viewModel.UpdateWindowPlacement(captured);
        }
    }

    private async Task CompleteShutdownAndCloseAsync()
    {
        try
        {
            await Task.Yield(); // Leave native Closing/ShutdownRequested callbacks before completing exit.
            await _shutdown!.ShutdownAsync();
            _shutdownAccepted = true;
            if (_exitApplication is { } exit) exit();
            else Close();
        }
        catch (DesktopShutdownException)
        {
            _shutdownRequestActive = false;
            TryActivateExistingWindow();
        }
    }
}
