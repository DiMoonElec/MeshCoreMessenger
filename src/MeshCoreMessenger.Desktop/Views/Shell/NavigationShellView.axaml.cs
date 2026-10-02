using Avalonia.Controls;
using Avalonia;

namespace MeshCoreMessenger.Desktop.Views.Shell;

public sealed partial class NavigationShellView : UserControl
{
    public static readonly StyledProperty<Control?> WorkspaceContentProperty =
        AvaloniaProperty.Register<NavigationShellView, Control?>(nameof(WorkspaceContent));
    public static readonly StyledProperty<bool> IsWorkspaceVisibleProperty =
        AvaloniaProperty.Register<NavigationShellView, bool>(nameof(IsWorkspaceVisible));

    public Control? WorkspaceContent { get => GetValue(WorkspaceContentProperty); set => SetValue(WorkspaceContentProperty, value); }
    public bool IsWorkspaceVisible { get => GetValue(IsWorkspaceVisibleProperty); set => SetValue(IsWorkspaceVisibleProperty, value); }
    public NavigationShellView() => InitializeComponent();
}
