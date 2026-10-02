using Avalonia.Controls;
using MeshCoreMessenger.Desktop.ViewModels;
namespace MeshCoreMessenger.Desktop.Views.Connection;
public sealed partial class ConnectionProfileEditorView : UserControl
{
    public ConnectionProfileEditorView() => InitializeComponent();
    private void OnProfileSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (DataContext is not ConnectionProfilesViewModel owner || sender is not ComboBox picker ||
            picker.SelectedItem is not ConnectionProfileListItem item || ReferenceEquals(item, owner.SelectedProfile) ||
            !owner.AvailableProfiles.Contains(item)) return;
        owner.SelectedProfile = item;
        // Dirty drafts can reject/defer selection. Keep the picker on the actual editor,
        // rather than relying on reentrant TwoWay binding notifications to roll it back.
        if (!ReferenceEquals(picker.SelectedItem, owner.SelectedProfile)) picker.SelectedItem = owner.SelectedProfile;
    }
}
