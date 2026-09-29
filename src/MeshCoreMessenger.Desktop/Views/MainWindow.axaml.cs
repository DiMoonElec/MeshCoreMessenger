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
