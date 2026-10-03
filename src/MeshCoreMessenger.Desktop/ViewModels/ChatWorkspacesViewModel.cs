using CommunityToolkit.Mvvm.ComponentModel;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.Lifecycle;
using System.ComponentModel;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Coordinates exactly two bounded workspaces over the same durable services.</summary>
public sealed class ChatWorkspacesViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly SemaphoreSlim _selectionWrites = new(1, 1);
    private ChatWorkspaceViewModel _active;
    private long _selectionRevision;
    private long _nodeRevision;
    private ShellSection _section = ShellSection.PublicChats;

    internal ChatWorkspacesViewModel(Func<MessengerNavigationTab, ConversationNavigationViewModel> create,
        Func<ConversationNavigationViewModel, ConversationListItem?, CancellationToken, Task> select,
        ISettingsStore settings, IUiDispatcher dispatcher, IOutgoingTextProcessor? textProcessor = null)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        Public = new(MessengerNavigationTab.Channels, create(MessengerNavigationTab.Channels), select, textProcessor);
        Private = new(MessengerNavigationTab.Personal, create(MessengerNavigationTab.Personal), select, textProcessor);
        _active = Public;
        foreach (var workspace in All) workspace.Navigation.PropertyChanged += OnNavigationChanged;
    }

    public ChatWorkspaceViewModel Public { get; }
    public ChatWorkspaceViewModel Private { get; }
    public ChatWorkspaceViewModel Active { get => _active; private set => SetProperty(ref _active, value); }
    private IEnumerable<ChatWorkspaceViewModel> All => [Public, Private];

    public void SelectSection(ShellSection section)
    {
        Interlocked.Increment(ref _selectionRevision);
        _section = section;
        foreach (var workspace in All)
        {
            workspace.Navigation.History.ReportVisibleRange(null, null, false, false);
            workspace.IsVisible = section == (ReferenceEquals(workspace, Public) ? ShellSection.PublicChats : ShellSection.PrivateChats);
        }
        if (section == ShellSection.PublicChats) Active = Public;
        if (section == ShellSection.PrivateChats) Active = Private;
    }

    public void SetNode(KnownNodeListItem? node)
    {
        Interlocked.Increment(ref _nodeRevision);
        foreach (var workspace in All)
        {
            workspace.ViewedNode = node;
            workspace.Navigation.ClearNode();
        }
    }

    public void UpdateNodeLabel(KnownNodeListItem node)
    {
        foreach (var workspace in All) workspace.ViewedNode = node;
    }

    public async Task LoadNodeAsync(Guid nodeId, CancellationToken token, bool dispatchResult)
    {
        var revision = Volatile.Read(ref _selectionRevision);
        var nodeRevision = Volatile.Read(ref _nodeRevision);
        var saved = await _settings.GetAsync(ConversationNavigationViewModel.TabSettingKey(nodeId), token);
        if (nodeRevision != Volatile.Read(ref _nodeRevision)) return;
        await Task.WhenAll(All.Select(workspace => workspace.Navigation.LoadNodeAsync(nodeId, token, dispatchResult)));
        void Apply()
        {
            if (nodeRevision != Volatile.Read(ref _nodeRevision) || Public.ViewedNode?.Id != nodeId || Private.ViewedNode?.Id != nodeId) return;
            if (revision == Volatile.Read(ref _selectionRevision))
            {
                Active = saved == MessengerNavigationTab.Channels.ToString() ? Public : Private;
                if (_section is ShellSection.PublicChats or ShellSection.PrivateChats)
                    _section = ReferenceEquals(Active, Public) ? ShellSection.PublicChats : ShellSection.PrivateChats;
            }
            foreach (var workspace in All)
            {
                workspace.IsVisible = _section == (ReferenceEquals(workspace, Public) ? ShellSection.PublicChats : ShellSection.PrivateChats);
                workspace.Navigation.History.ReportVisibleRange(null, null, false, false);
            }
        }
        if (dispatchResult) await _dispatcher.InvokeAsync(Apply, token); else Apply();
    }

    public async Task SaveActiveSectionAsync(CancellationToken token)
    {
        await _selectionWrites.WaitAsync(token);
        try
        {
            if (Active.ViewedNode is { } node)
                await _settings.SetAsync(ConversationNavigationViewModel.TabSettingKey(node.Id), Active.Navigation.SelectedTab.Tab.ToString(), token);
        }
        finally { _selectionWrites.Release(); }
    }

    public async Task RefreshAsync(CancellationToken token)
    {
        foreach (var workspace in All)
        {
            await workspace.Navigation.RefreshAsync(token);
            if (!workspace.IsVisible) workspace.Navigation.History.ReportVisibleRange(null, null, false, false);
        }
    }

    public async Task HandleCommittedMessageAsync(StoredIncomingMessage message, CancellationToken token)
    {
        foreach (var workspace in All) await workspace.Navigation.HandleCommittedMessageAsync(message, token);
    }

    public async Task StopAsync()
    {
        foreach (var workspace in All)
        {
            workspace.IsVisible = false;
            workspace.Navigation.History.ReportVisibleRange(null, null, false, false);
            workspace.Navigation.PropertyChanged -= OnNavigationChanged;
        }
        await Task.WhenAll(All.Select(workspace => workspace.Navigation.StopAsync()));
    }

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (ReferenceEquals(sender, Active.Navigation)) OnPropertyChanged(nameof(Active));
    }
}
