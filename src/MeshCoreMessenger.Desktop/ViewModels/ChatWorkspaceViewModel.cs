using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Application;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Presentation owner of one retained chat section; no session or storage ownership.</summary>
public sealed class ChatWorkspaceViewModel : ObservableObject
{
    private KnownNodeListItem? _viewedNode;
    private bool _isVisible;
    private HistoryClearViewModel _historyClear = new(() => null, null, _ => Task.CompletedTask);
    private readonly Func<ConversationNavigationViewModel, ConversationListItem?, CancellationToken, Task> _select;

    internal ChatWorkspaceViewModel(MessengerNavigationTab kind, ConversationNavigationViewModel navigation,
        Func<ConversationNavigationViewModel, ConversationListItem?, CancellationToken, Task> select,
        IOutgoingTextProcessor? textProcessor = null)
    {
        Navigation = navigation;
        DetailsCommand = new RelayCommand(() => DetailsRequested?.Invoke(this, EventArgs.Empty),
            () => ViewedNode is { } node && SelectedConversation is { } conversation && conversation.NodeId == node.Id &&
                conversation.Entry.Identity.Length == 32 && conversation.Kind ==
                    (kind == MessengerNavigationTab.Personal ? ConversationKind.Contact : ConversationKind.Channel));
        Menu = new ConversationMenuViewModel(kind, _historyClear, RouteReset, DetailsCommand);
        navigation.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ConversationNavigationViewModel.SelectedConversation))
            {
                RouteReset.Invalidate();
                DetailsCommand.NotifyCanExecuteChanged();
            }
        };
        Composer = new ComposerViewModel(navigation.Draft, textProcessor);
        _select = select;
    }

    public ConversationNavigationViewModel Navigation { get; }
    public ComposerViewModel Composer { get; }
    public ContactRouteResetViewModel RouteReset { get; } = new();
    public ConversationMenuViewModel Menu { get; }
    public RelayCommand DetailsCommand { get; }
    public event EventHandler? DetailsRequested;
    public HistoryClearViewModel HistoryClear
    {
        get => _historyClear;
        internal set
        {
            _historyClear = value;
            Menu.SetHistoryClear(value);
        }
    }
    public ConversationListItem? SelectedConversation => Navigation.SelectedConversation;
    public KnownNodeListItem? ViewedNode
    {
        get => _viewedNode;
        internal set
        {
            if (SetProperty(ref _viewedNode, value))
            {
                OnPropertyChanged(nameof(ViewedNodePublicKeyHex));
                DetailsCommand.NotifyCanExecuteChanged();
                RouteReset.Invalidate();
            }
        }
    }
    public string? ViewedNodePublicKeyHex => ViewedNode?.PublicKeyHex;
    public bool IsVisible { get => _isVisible; internal set => SetProperty(ref _isVisible, value); }
    public Task SelectConversationAsync(ConversationListItem? item, CancellationToken cancellationToken = default) =>
        _select(Navigation, item, cancellationToken);
}
