using Avalonia.Controls;
using Avalonia.Input;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop.Views.Chat;

public sealed partial class ConversationListView : UserControl
{
    public ConversationListView()
    {
        InitializeComponent();
        ConversationList.PointerReleased += async (_, _) =>
        {
            if (DataContext is ChatWorkspaceViewModel owner && ConversationList.SelectedItem is ConversationListItem item &&
                ReferenceEquals(item, owner.SelectedConversation))
            {
                try { await owner.SelectConversationAsync(item); }
                catch (OperationCanceledException) { }
            }
        };
        ConversationList.KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Enter && DataContext is ChatWorkspaceViewModel owner && ConversationList.SelectedItem is ConversationListItem item)
            {
                args.Handled = true;
                try { await owner.SelectConversationAsync(item); }
                catch (OperationCanceledException) { }
            }
        };
    }
    public void FocusSearch() { DirectorySearch.Focus(); DirectorySearch.SelectAll(); }
    public bool IsSearchFocused => DirectorySearch.IsFocused;

    private async void OnSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        // Collection refresh nulls are not user requests to close a conversation.
        if (DataContext is ChatWorkspaceViewModel owner && ConversationList.SelectedItem is ConversationListItem item &&
            !ReferenceEquals(item, owner.SelectedConversation))
        {
            try { await owner.SelectConversationAsync(item); }
            catch (OperationCanceledException) { }
        }
    }
}
