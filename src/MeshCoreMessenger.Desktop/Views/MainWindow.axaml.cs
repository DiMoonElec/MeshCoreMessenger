using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private readonly IDesktopShutdownCoordinator? _shutdown;
    private bool _shutdownAccepted;
    private bool _shutdownRequestActive;
    private HistoryWindowViewModel? _subscribedHistory;

    public MainWindow()
    {
        InitializeComponent();
        HistoryList.AddHandler(ScrollViewer.ScrollChangedEvent, OnHistoryScrollChanged);
        DataContextChanged += OnWindowDataContextChanged;
        Activated += OnWindowActivationChanged;
        Deactivated += OnWindowActivationChanged;
        Opened += (_, _) => Dispatcher.UIThread.Post(ScrollHistoryToEnd);
    }

    public MainWindow(IDesktopShutdownCoordinator shutdown)
        : this()
    {
        _shutdown = shutdown;
    }

    protected override void OnClosing(WindowClosingEventArgs eventArgs)
    {
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

        base.OnClosed(eventArgs);
    }

    private void OnWindowDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (_subscribedHistory is not null)
        {
            _subscribedHistory.ScrollRequested -= OnHistoryScrollRequested;
        }

        _subscribedHistory = (DataContext as MainWindowViewModel)?.Navigation.History;
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
        if (eventArgs.Key != Key.F ||
            (eventArgs.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0)
        {
            return;
        }

        var history = (DataContext as MainWindowViewModel)?.Navigation;
        var target = history?.HasSelection == true && DetailPane.IsEffectivelyVisible
            ? HistorySearchBox
            : DirectorySearchBox;
        target.Focus();
        target.SelectAll();
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
