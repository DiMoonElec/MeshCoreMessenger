using Avalonia;
using Avalonia.Controls;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private readonly IDesktopShutdownCoordinator? _shutdown;
    private bool _shutdownAccepted;
    private bool _shutdownRequestActive;

    public MainWindow() => InitializeComponent();

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

    private async void OnConversationSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (DataContext is not MainWindowViewModel viewModel || sender is not ListBox listBox)
        {
            return;
        }

        try
        {
            await viewModel.SelectConversationAsync(listBox.SelectedItem as ConversationListItem);
        }
        catch (OperationCanceledException)
        {
        }
    }

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
