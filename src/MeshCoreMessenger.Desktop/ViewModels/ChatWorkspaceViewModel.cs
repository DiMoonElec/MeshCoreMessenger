using CommunityToolkit.Mvvm.ComponentModel;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Presentation owner of one retained chat section; no session or storage ownership.</summary>
public sealed class ChatWorkspaceViewModel : ObservableObject
{
    private KnownNodeListItem? _viewedNode;
    private bool _isVisible;
    private readonly Func<ConversationNavigationViewModel, ConversationListItem?, CancellationToken, Task> _select;

    internal ChatWorkspaceViewModel(ConversationNavigationViewModel navigation,
        Func<ConversationNavigationViewModel, ConversationListItem?, CancellationToken, Task> select)
    {
        Navigation = navigation;
        Composer = new ComposerViewModel(navigation.Draft);
        _select = select;
    }

    public ConversationNavigationViewModel Navigation { get; }
    public ComposerViewModel Composer { get; }
    public ConversationListItem? SelectedConversation => Navigation.SelectedConversation;
    public KnownNodeListItem? ViewedNode
    {
        get => _viewedNode;
        internal set
        {
            if (SetProperty(ref _viewedNode, value)) OnPropertyChanged(nameof(ViewedNodePublicKeyHex));
        }
    }
    public string? ViewedNodePublicKeyHex => ViewedNode?.PublicKeyHex;
    public bool IsVisible { get => _isVisible; internal set => SetProperty(ref _isVisible, value); }
    public Task SelectConversationAsync(ConversationListItem? item, CancellationToken cancellationToken = default) =>
        _select(Navigation, item, cancellationToken);
}
