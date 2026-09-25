using Avalonia.Controls;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

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
}
