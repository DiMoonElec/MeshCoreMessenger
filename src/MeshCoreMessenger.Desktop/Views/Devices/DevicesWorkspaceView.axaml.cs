using Avalonia;
using Avalonia.Controls;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop.Views.Devices;

public sealed partial class DevicesWorkspaceView : UserControl
{
    public DevicesWorkspaceView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutMode();
        DataContextChanged += (_, _) => UpdateLayoutMode();
    }
    private void UpdateLayoutMode()
    {
        if (DataContext is not DevicesWorkspaceViewModel vm || Bounds.Width <= 0) return;
        var narrow = Bounds.Width < (double)this.FindResource("ChatNarrowWidth")!;
        vm.SetNarrowLayout(narrow);
        Workspace.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : (GridLength)this.FindResource("ChatListWidth")!;
        Workspace.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(Detail, narrow ? 0 : 1);
        Grid.SetColumnSpan(Detail, narrow ? 2 : 1);
    }
}
