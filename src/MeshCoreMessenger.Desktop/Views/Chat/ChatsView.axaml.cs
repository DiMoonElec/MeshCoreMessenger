using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop.Views.Chat;

/// <summary>One persistent chat workspace, sharing the existing node-scoped navigation/history/draft.</summary>
public sealed partial class ChatsView : UserControl
{
    public ChatsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateWorkspaceLayout();
        DataContextChanged += (_, _) => UpdateWorkspaceLayout();
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (!IsEffectivelyVisible || DataContext is not MainWindowViewModel owner)
            return;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var action = DesktopShortcutRouter.Route(new DesktopShortcutContext(args.Key, args.KeyModifiers,
            focused is TextBox && !Directory.IsSearchFocused && !Detail.IsSearchFocused,
            owner.Navigation.HasSelection, Detail.IsEffectivelyVisible, false,
            owner.Navigation.IsDirectorySearchActive, owner.Navigation.History.IsSearchActive, owner.Navigation.CanNavigateBack));
        switch (action)
        {
            case DesktopShortcutAction.FocusDirectorySearch: Directory.FocusSearch(); break;
            case DesktopShortcutAction.FocusHistorySearch: Detail.FocusSearch(); break;
            case DesktopShortcutAction.ClearDirectorySearch: owner.Navigation.DirectorySearchText = string.Empty; break;
            case DesktopShortcutAction.ClearHistorySearch: owner.Navigation.History.SearchText = string.Empty; break;
            case DesktopShortcutAction.NavigateBack: owner.Navigation.BackCommand.Execute(null); break;
            default: return;
        }
        args.Handled = true;
    }

    private void UpdateWorkspaceLayout()
    {
        if (DataContext is not MainWindowViewModel owner || Bounds.Width <= 0)
            return;
        var threshold = (double)this.FindResource("ChatNarrowWidth")!;
        var narrow = Bounds.Width < threshold;
        owner.Navigation.SetNarrowLayout(narrow);
        Workspace.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : (GridLength)this.FindResource("ChatListWidth")!;
        Workspace.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(Detail, narrow ? 0 : 1);
        Grid.SetColumnSpan(Detail, narrow ? 2 : 1);
    }
}
