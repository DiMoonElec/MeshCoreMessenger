using Avalonia.Controls;
using Avalonia.Input;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop.Views.Devices;

public sealed partial class DeviceListView : UserControl
{
    public DeviceListView()
    {
        InitializeComponent();
        DeviceList.PointerReleased += async (_, _) => await OpenSelectedAsync();
        DeviceList.KeyDown += async (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true; await OpenSelectedAsync();
        };
    }
    private async Task OpenSelectedAsync()
    {
        if (DataContext is DevicesWorkspaceViewModel vm && DeviceList.SelectedItem is DeviceListItem item)
            await vm.SelectAsync(item);
    }
    private async void OnSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (DataContext is DevicesWorkspaceViewModel vm && DeviceList.SelectedItem is DeviceListItem item &&
            !ReferenceEquals(item, vm.Selected)) await vm.SelectAsync(item);
    }
}
