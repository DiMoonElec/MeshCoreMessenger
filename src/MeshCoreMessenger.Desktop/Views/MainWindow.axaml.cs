using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Preferences;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private readonly IDesktopShutdownCoordinator? _shutdown;
    private bool _shutdownAccepted;
    private bool _shutdownRequestActive;
    private bool _placementInitialized;
    private WindowPlacement? _normalPlacement;
    private HistoryWindowViewModel? _subscribedHistory;
    private MainWindowViewModel? _subscribedViewModel;

    public MainWindow()
    {
        InitializeComponent();
        HistoryList.AddHandler(ScrollViewer.ScrollChangedEvent, OnHistoryScrollChanged);
        DataContextChanged += OnWindowDataContextChanged;
        Activated += OnWindowActivationChanged;
        Deactivated += OnWindowActivationChanged;
        PositionChanged += (_, _) => CaptureWindowPlacement();
        PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.Property == WindowStateProperty)
            {
                CaptureWindowPlacement();
            }
        };
        Opened += (_, _) =>
        {
            ApplySavedWindowPlacement();
            Dispatcher.UIThread.Post(ScrollHistoryToEnd);
        };
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
        if (_shutdownRequestActive)
        {
            return;
        }

        _shutdownRequestActive = true;
        _ = CompleteShutdownAndCloseAsync();
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        if (_subscribedHistory is not null)
        {
            _subscribedHistory.ScrollRequested -= OnHistoryScrollRequested;
            _subscribedHistory = null;
        }

        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        }

        base.OnClosed(eventArgs);
    }

    private void OnWindowDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (_subscribedHistory is not null)
        {
            _subscribedHistory.ScrollRequested -= OnHistoryScrollRequested;
        }

        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _subscribedViewModel = DataContext as MainWindowViewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ApplyTheme(_subscribedViewModel.SelectedTheme.Value);
        }

        _subscribedHistory = _subscribedViewModel?.Navigation.History;
        if (_subscribedHistory is not null)
        {
            _subscribedHistory.ScrollRequested += OnHistoryScrollRequested;
            Dispatcher.UIThread.Post(() => ScrollHistoryToEnd());
        }
    }

    private void OnHistoryScrollRequested(object? sender, HistoryScrollRequestEventArgs eventArgs)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (eventArgs.ScrollToEnd)
            {
                ScrollHistoryToEnd();
                return;
            }

            if (eventArgs.AnchorSequence is { } sequence)
            {
                var anchor = HistoryList.Items
                    .OfType<HistoryMessageListItem>()
                    .FirstOrDefault(item => item.LocalSequence == sequence);
                if (anchor is not null)
                {
                    HistoryList.ScrollIntoView(anchor);
                }
            }
        });
    }

    private void OnHistoryScrollChanged(object? sender, ScrollChangedEventArgs eventArgs)
    {
        ReportHistoryViewport(eventArgs.Source as ScrollViewer);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var action = DesktopShortcutRouter.Route(new DesktopShortcutContext(
            eventArgs.Key,
            eventArgs.KeyModifiers,
            focused is TextBox && focused != DirectorySearchBox && focused != HistorySearchBox,
            viewModel.Navigation.HasSelection,
            DetailPane.IsEffectivelyVisible,
            viewModel.IsConnectionSettingsOpen,
            !string.IsNullOrEmpty(viewModel.Navigation.DirectorySearchText),
            !string.IsNullOrEmpty(viewModel.Navigation.History.SearchText),
            viewModel.Navigation.CanNavigateBack));
        switch (action)
        {
            case DesktopShortcutAction.FocusDirectorySearch:
                DirectorySearchBox.Focus();
                DirectorySearchBox.SelectAll();
                break;
            case DesktopShortcutAction.FocusHistorySearch:
                HistorySearchBox.Focus();
                HistorySearchBox.SelectAll();
                break;
            case DesktopShortcutAction.CloseSettings:
                viewModel.CloseConnectionSettingsCommand.Execute(null);
                break;
            case DesktopShortcutAction.ClearDirectorySearch:
                viewModel.Navigation.DirectorySearchText = string.Empty;
                break;
            case DesktopShortcutAction.ClearHistorySearch:
                viewModel.Navigation.History.SearchText = string.Empty;
                break;
            case DesktopShortcutAction.NavigateBack:
                viewModel.Navigation.BackCommand.Execute(null);
                break;
            case DesktopShortcutAction.None:
                return;
        }

        eventArgs.Handled = true;
    }

    private async void OnHistorySearchResultSelectionChanged(
        object? sender,
        SelectionChangedEventArgs eventArgs)
    {
        if (DataContext is not MainWindowViewModel viewModel ||
            sender is not ListBox listBox ||
            listBox.SelectedItem is not HistorySearchResultListItem result)
        {
            return;
        }

        try
        {
            if (await viewModel.Navigation.History.JumpToSearchResultAsync(result))
            {
                viewModel.Navigation.History.DismissSearchResults();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listBox.SelectedItem = null;
        }
    }

    private void OnWindowActivationChanged(object? sender, EventArgs eventArgs) =>
        ReportHistoryViewport();

    private void ReportHistoryViewport(ScrollViewer? scrollViewer = null)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var visible = HistoryList.GetRealizedContainers()
            .Select(container => new
            {
                Index = HistoryList.IndexFromContainer(container),
                Origin = container.TranslatePoint(default, HistoryList),
                Height = container.Bounds.Height,
            })
            .Where(item =>
                item.Index >= 0 &&
                item.Origin is { } origin &&
                origin.Y + item.Height > 0 &&
                origin.Y < HistoryList.Bounds.Height)
            .Select(item => item.Index)
            .Order()
            .ToArray();
        var firstIndex = visible.FirstOrDefault(-1);
        var lastIndex = visible.LastOrDefault(-1);
        var first = firstIndex >= 0 && firstIndex < viewModel.Navigation.History.Messages.Count
            ? viewModel.Navigation.History.Messages[firstIndex].LocalSequence
            : (long?)null;
        var last = lastIndex >= 0 && lastIndex < viewModel.Navigation.History.Messages.Count
            ? viewModel.Navigation.History.Messages[lastIndex].LocalSequence
            : (long?)null;
        var isAtEnd = scrollViewer is null
            ? lastIndex == viewModel.Navigation.History.Messages.Count - 1
            : scrollViewer.Offset.Y + scrollViewer.Viewport.Height >= scrollViewer.Extent.Height - 2;
        viewModel.Navigation.History.ReportVisibleRange(
            first,
            last,
            IsActive && HistoryList.IsEffectivelyVisible,
            isAtEnd);

        if (firstIndex is >= 0 and <= 2 && viewModel.Navigation.History.CanLoadOlder)
        {
            viewModel.Navigation.History.LoadOlderCommand.Execute(null);
        }

        if (isAtEnd && viewModel.Navigation.History.CanLoadNewer)
        {
            viewModel.Navigation.History.LoadNewerCommand.Execute(null);
        }
    }

    private void ScrollHistoryToEnd()
    {
        if (HistoryList.ItemCount > 0)
        {
            HistoryList.ScrollIntoView(HistoryList.ItemCount - 1);
        }
    }

    private async void OnConversationSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (DataContext is not MainWindowViewModel viewModel ||
            sender is not ListBox listBox ||
            ConversationSelectionToApply(listBox.SelectedItem) is not { } conversation)
        {
            return;
        }

        try
        {
            await viewModel.SelectConversationAsync(conversation);
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Collection refreshes briefly clear ListBox.SelectedItem. That is not a user request to
    // close the current conversation and must not race the restored stable-key selection.
    internal static ConversationListItem? ConversationSelectionToApply(object? selectedItem) =>
        selectedItem as ConversationListItem;

    private async void OnNavigationTabSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (DataContext is not MainWindowViewModel viewModel ||
            sender is not ListBox listBox ||
            listBox.SelectedItem is not MessengerNavigationTabItem tab ||
            tab.Tab == viewModel.Navigation.SelectedTab.Tab)
        {
            return;
        }

        try
        {
            await viewModel.SelectNavigationTabAsync(tab);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var narrow = eventArgs.NewSize.Width < 760;
        viewModel.SetNarrowLayout(narrow);
        ConversationColumns.ColumnDefinitions[0].Width = narrow
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(340);
        ConversationColumns.ColumnDefinitions[1].Width = narrow
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(DetailPane, narrow ? 0 : 1);
        Grid.SetColumnSpan(DetailPane, narrow ? 2 : 1);
        CaptureWindowPlacement();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
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

    private void ApplySavedWindowPlacement()
    {
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
        if (!_placementInitialized || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        WindowPlacement? currentNormal = null;
        if (WindowState == WindowState.Normal)
        {
            var scale = RenderScaling is > 0 ? RenderScaling : 1;
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

    private async void OnNodeSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (DataContext is not MainWindowViewModel viewModel || sender is not ComboBox comboBox)
        {
            return;
        }

        var selectedNode = comboBox.SelectedItem as KnownNodeListItem;
        if (selectedNode?.Id == viewModel.ViewedNode?.Id)
        {
            return;
        }

        try
        {
            await viewModel.SelectViewedNodeAsync(selectedNode);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CompleteShutdownAndCloseAsync()
    {
        try
        {
            await _shutdown!.ShutdownAsync();
            _shutdownAccepted = true;
            Close();
        }
        catch (DesktopShutdownException)
        {
            _shutdownRequestActive = false;
        }
    }
}
