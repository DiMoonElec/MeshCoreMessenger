using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Preferences;

namespace MeshCoreMessenger.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDesktopUiLifetime
{
    internal const string ViewedNodeSettingKey = "desktop.viewed-node-id";
    internal const string LastConnectedNodeSettingKey = "desktop.last-connected-node-id";

    private const int KnownNodePageSize = 500;
    private readonly INodeStore _nodes;
    private readonly ISettingsStore _settings;
    private readonly DesktopPreferences _preferences;
    private readonly IConnectionSupervisor _supervisor;
    private readonly IMessageCommitNotifications _commitNotifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly ComposerContextCoordinator[] _composerContexts;
    private readonly ChannelRepeatCoordinator _channelRepeats;
    private readonly PrivateResendCoordinator _privateResends;
    private readonly IOutgoingMessageStore? _outgoingMessages;
    private readonly IDirectoryStore? _directoryUpdates;
    private readonly ConcurrentQueue<OutgoingMessageCommit> _pendingOutgoing = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _projectionRefreshSignal = new(0);
    private readonly SemaphoreSlim _viewSelectionPersistence = new(1, 1);
    private readonly ConcurrentQueue<StoredIncomingMessage> _pendingCommittedMessages = new();
    private readonly object _pendingLoadsGate = new();
    private readonly HashSet<Task> _pendingLoads = [];
    private readonly Task _projectionRefreshWorker;
    private KnownNodeListItem? _viewedNode;
    private KnownNodeListItem? _activeNode;
    private string _status = "Загрузка локальной истории…";
    private string _connectionStatus;
    private string? _connectionStatusDetail;
    private string _activeProfileDisplayName = "Не выбран";
    private string? _errorMessage;
    private string? _preferencesSaveError;
    private bool _isLoading;
    private ConnectionSupervisorState _connectionState;
    private bool _isConnectionSettingsOpen;
    private long _viewContextVersion;
    private long _connectionStateVersion;
    private int _projectionRefreshRequested;
    private int _stopped;
    private bool _syncingShell;
    private long _shellRequestVersion;

    public MainWindowViewModel(
        IConversationDirectoryReader directory,
        ILocalHistoryReader history,
        IConversationReadStateStore readStates,
        IDurableReadStateWrites readWrites,
        IDraftBuffer drafts,
        INodeStore nodes,
        ISettingsStore settings,
        DesktopPreferences preferences,
        ConnectionProfilesViewModel profiles,
        IConnectionSupervisor supervisor,
        IMessageCommitNotifications commitNotifications,
        IUiDispatcher dispatcher,
        ISearchDelay searchDelay,
        IDraftDelay draftDelay,
        ILogger<MainWindowViewModel> logger,
        IOutgoingTextProcessor? textProcessor = null,
        ISendReadinessReader? sendReadiness = null,
        IMessageService? messageService = null, IOutgoingMessageStore? outgoingMessages = null, IHistoryClearService? historyClear = null, IContactRouteService? contactRoutes = null,
        IDirectoryStore? directoryUpdates = null, IContactDeliveryHistoryReader? contactDeliveries = null)
    {
        _outgoingMessages = outgoingMessages;
        if (outgoingMessages is not null) outgoingMessages.MessageCommitted += OnOutgoingCommitted;
        _directoryUpdates = directoryUpdates;
        if (directoryUpdates is not null) directoryUpdates.ContactRouteCommitted += OnContactRouteCommitted;
        _nodes = nodes;
        _settings = settings;
        _preferences = preferences;
        Profiles = profiles;
        _supervisor = supervisor;
        _commitNotifications = commitNotifications;
        _dispatcher = dispatcher;
        _logger = logger;
        _connectionState = supervisor.Snapshot.State;
        Devices = new DevicesWorkspaceViewModel(directory, dispatcher, searchDelay, logger);
        Chats = new ChatWorkspacesViewModel(tab => new ConversationNavigationViewModel(
            directory, history, readStates, readWrites, drafts, settings, dispatcher, searchDelay, draftDelay, logger, tab),
            (navigation, item, token) => Track(SelectConversationCoreAsync(navigation, item, token)), settings, dispatcher, textProcessor);
        foreach (var workspace in new[] { Chats.Public, Chats.Private })
            workspace.HistoryClear = new(() => workspace.ViewedNode is { } node && workspace.SelectedConversation is { Id: { } id } conversation
                ? new HistoryClearTarget(node.Id, id, node.HeaderLabel, conversation.Title) : null,
                historyClear, ApplyHistoryClearAsync, Track, _lifetimeCancellation.Token);
        Chats.Private.RouteReset.Configure(() =>
        {
            var snapshot = _supervisor.Snapshot;
            var workspace = Chats.Private;
            if (snapshot.State != ConnectionSupervisorState.Online || snapshot.SessionId is not { } session ||
                workspace.ViewedNode?.Id != snapshot.NodeId || workspace.SelectedConversation is not { Kind: ConversationKind.Contact } contact ||
                contact.Entry.Identity.Length != 32) return null;
            return new ContactRouteResetRequest(contact.NodeId, session, snapshot.Generation, contact.Entry.Identity);
        }, contactRoutes, async result =>
        {
            await Chats.RefreshAsync(_lifetimeCancellation.Token);
            await Devices.RefreshAsync(_lifetimeCancellation.Token);
        }, Track, _lifetimeCancellation.Token);
        Chats.Private.DetailsRequested += (_, _) =>
        {
            if (Modal.IsOpen || Volatile.Read(ref _stopped) != 0 || !Chats.Private.DetailsCommand.CanExecute(null)) return;
            var contact = Chats.Private.SelectedConversation!;
            var card = new ContactDetailsCardViewModel(contact.NodeId, contact.Entry.Identity,
                directory, directoryUpdates, dispatcher, logger, Track, contactDeliveries);
            _ = Track(Modal.ShowAsync(card));
            _ = Track(card.RefreshAsync());
        };
        Chats.Public.DetailsRequested += (_, _) =>
        {
            if (Modal.IsOpen || Volatile.Read(ref _stopped) != 0 || !Chats.Public.DetailsCommand.CanExecute(null)) return;
            var channel = Chats.Public.SelectedConversation!;
            var card = new ChannelDetailsCardViewModel(channel.NodeId, channel.Entry.Identity, directory, dispatcher, logger, Track);
            _ = Track(Modal.ShowAsync(card));
            _ = Track(card.RefreshAsync());
        };
        _composerContexts = [new(Chats.Public, supervisor, sendReadiness, dispatcher, logger, messageService),
            new(Chats.Private, supervisor, sendReadiness, dispatcher, logger, messageService)];
        _channelRepeats = new(Chats.Public, supervisor, messageService, outgoingMessages, dispatcher, Track, _lifetimeCancellation.Token, logger);
        _privateResends = new(Chats.Private, supervisor, messageService, dispatcher, Track, _lifetimeCancellation.Token, logger);
        (_connectionStatus, _connectionStatusDetail) = DescribeConnection(supervisor.Snapshot);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync);
        RetryPreferencesSaveCommand = new AsyncRelayCommand(
            token => Track(SavePreferencesAsync(token)),
            () => HasPreferencesSaveError && Volatile.Read(ref _stopped) == 0);
        ToggleConnectionSettingsCommand = new RelayCommand(
            () => IsConnectionSettingsOpen = !IsConnectionSettingsOpen);
        CloseConnectionSettingsCommand = new RelayCommand(() => IsConnectionSettingsOpen = false);
        _supervisor.StateChanged += OnSupervisorStateChanged;
        _commitNotifications.MessageCommitted += OnMessageCommitted;
        Shell.PropertyChanged += OnShellPropertyChanged;
        Chats.PropertyChanged += OnNavigationPropertyChanged;
        _projectionRefreshWorker = Track(ProcessProjectionRefreshesAsync());
    }

    public string Title => $"MeshCore Messenger - {(_connectionState == ConnectionSupervisorState.Offline ? "Отключено" : ConnectionStatus)}" +
        (_connectionState == ConnectionSupervisorState.Online && ActiveNode is { } node ? $" [{node.HeaderLabel}]" : string.Empty);

    public ModalHostViewModel Modal { get; } = new();
    public NavigationShellViewModel Shell { get; } = new();
    public DevicesWorkspaceViewModel Devices { get; }
    public ChatWorkspacesViewModel Chats { get; }
    public bool IsChatWorkspaceVisible => Shell.IsChatSelected && Navigation.SelectedTab.Tab ==
        (Shell.SelectedItem.Section == ShellSection.PublicChats ? MessengerNavigationTab.Channels : MessengerNavigationTab.Personal);

    public IReadOnlyList<DesktopThemeOption> ThemeOptions { get; } =
    [
        new(DesktopThemePreference.System, "Авто"),
        new(DesktopThemePreference.Dark, "Тёмная"),
        new(DesktopThemePreference.Light, "Светлая"),
    ];

    public DesktopThemeOption SelectedTheme
    {
        get => ThemeOptions.Single(option => option.Value == _preferences.Snapshot.Theme);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (Volatile.Read(ref _stopped) != 0 || value.Value == _preferences.Snapshot.Theme)
            {
                return;
            }

            _preferences.SetTheme(value.Value);
            OnPropertyChanged();
            RequestPreferencesSave();
        }
    }

    public IReadOnlyList<DesktopCloseBehaviorOption> CloseBehaviorOptions { get; } =
    [
        new(DesktopCloseBehavior.MinimizeToTray, "Сворачивать в трей"),
        new(DesktopCloseBehavior.ExitApplication, "Закрывать приложение"),
    ];

    public DesktopCloseBehaviorOption SelectedCloseBehavior
    {
        get => CloseBehaviorOptions.Single(option => option.Value == _preferences.Snapshot.CloseBehavior);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (Volatile.Read(ref _stopped) != 0 || value.Value == _preferences.Snapshot.CloseBehavior) return;
            _preferences.SetCloseBehavior(value.Value);
            OnPropertyChanged();
            RequestPreferencesSave();
        }
    }

    public bool NotifyPrivateMessages
    {
        get => _preferences.Snapshot.NotifyPrivateMessages;
        set
        {
            if (Volatile.Read(ref _stopped) != 0 || value == NotifyPrivateMessages) return;
            _preferences.SetNotifyPrivateMessages(value);
            OnPropertyChanged();
            RequestPreferencesSave();
        }
    }

    public bool NotifyChannelMessages
    {
        get => _preferences.Snapshot.NotifyChannelMessages;
        set
        {
            if (Volatile.Read(ref _stopped) != 0 || value == NotifyChannelMessages) return;
            _preferences.SetNotifyChannelMessages(value);
            OnPropertyChanged();
            RequestPreferencesSave();
        }
    }

    public string? PreferencesSaveError
    {
        get => _preferencesSaveError;
        private set
        {
            if (!SetProperty(ref _preferencesSaveError, value)) return;
            OnPropertyChanged(nameof(HasPreferencesSaveError));
            RetryPreferencesSaveCommand.NotifyCanExecuteChanged();
        }
    }
    public bool HasPreferencesSaveError => PreferencesSaveError is not null;
    public IAsyncRelayCommand RetryPreferencesSaveCommand { get; }

    private void RequestPreferencesSave()
    {
        if (Volatile.Read(ref _stopped) == 0) _ = Track(SavePreferencesAsync());
    }

    private async Task SavePreferencesAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        try
        {
            try { await _preferences.FlushAsync(linked.Token).ConfigureAwait(false); }
            catch (DesktopPreferencesPersistenceException exception)
            { _logger.LogWarning(exception, "Could not save desktop preferences."); }

            await _dispatcher.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _stopped) != 0) return;
                // Read the current writer state: an older completion must not overwrite a newer failure/recovery.
                PreferencesSaveError = _preferences.IsPaused
                    ? "Не удалось сохранить настройки. Повторите сохранение."
                    : null;
            }, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
    }

    public WindowPlacement? SavedWindowPlacement => _preferences.Snapshot.WindowPlacement;

    public void UpdateWindowPlacement(WindowPlacement placement) =>
        _preferences.SetWindowPlacement(placement);

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public string? ConnectionStatusDetail
    {
        get => _connectionStatusDetail;
        private set
        {
            if (SetProperty(ref _connectionStatusDetail, value))
            {
                OnPropertyChanged(nameof(HasConnectionStatusDetail));
            }
        }
    }

    public bool HasConnectionStatusDetail => !string.IsNullOrWhiteSpace(ConnectionStatusDetail);

    public string ActiveProfileDisplayName
    {
        get => _activeProfileDisplayName;
        private set => SetProperty(ref _activeProfileDisplayName, value);
    }

    public KnownNodeListItem? ActiveNode
    {
        get => _activeNode;
        private set
        {
            if (SetProperty(ref _activeNode, value))
            {
                OnPropertyChanged(nameof(HasActiveNode));
                OnPropertyChanged(nameof(ActiveNodeDisplayName));
                OnPropertyChanged(nameof(ActiveNodePublicKeyHex));
            }
        }
    }

    public bool HasActiveNode => ActiveNode is not null;
    public string ActiveNodeDisplayName => ActiveNode?.HeaderLabel ?? "Не определена";
    public string? ActiveNodePublicKeyHex => ActiveNode?.PublicKeyHex;

    public KnownNodeListItem? ViewedNode
    {
        get => _viewedNode;
        private set
        {
            if (SetProperty(ref _viewedNode, value))
            {
                OnPropertyChanged(nameof(HasViewedNode));
                OnPropertyChanged(nameof(ViewedNodePublicKeyHex));
            }
        }
    }

    public bool HasViewedNode => ViewedNode is not null;
    public string? ViewedNodePublicKeyHex => ViewedNode?.PublicKeyHex;

    public bool CanSelectViewedNode => false;

    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand DisconnectCommand { get; }
    public IRelayCommand ToggleConnectionSettingsCommand { get; }
    public IRelayCommand CloseConnectionSettingsCommand { get; }
    public ConnectionProfilesViewModel Profiles { get; }
    public ConversationNavigationViewModel Navigation => Chats.Active.Navigation;
    public ObservableCollection<KnownNodeListItem> KnownNodes { get; } = [];
    public ObservableCollection<ConversationListItem> Conversations => Navigation.Conversations;
    public ObservableCollection<HistoryMessageListItem> Messages => Navigation.Messages;
    public ConversationListItem? SelectedConversation => Navigation.SelectedConversation;

    public bool IsConnectionSettingsOpen
    {
        get => _isConnectionSettingsOpen;
        private set => SetProperty(ref _isConnectionSettingsOpen, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => ErrorMessage is not null;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorMessage = null;
        Status = "Загрузка локальной истории…";

        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            await _preferences.LoadAsync(linkedCancellation.Token);
            OnPropertyChanged(nameof(SelectedTheme));
            OnPropertyChanged(nameof(SelectedCloseBehavior));
            OnPropertyChanged(nameof(NotifyPrivateMessages));
            OnPropertyChanged(nameof(NotifyChannelMessages));
            OnPropertyChanged(nameof(SavedWindowPlacement));
            await Profiles.LoadAsync(linkedCancellation.Token);
            var nodes = await _nodes.GetAllAsync(KnownNodePageSize, linkedCancellation.Token);
            var viewedNodeSetting = await _settings.GetAsync(LastConnectedNodeSettingKey, linkedCancellation.Token);

            ReplaceKnownNodes(nodes);

            var snapshot = _supervisor.Snapshot;
            var activeNode = await ReadSnapshotNodeAsync(snapshot, linkedCancellation.Token);
            ApplyConnectionPresentation(snapshot, activeNode);

            KnownNodeListItem? viewedNode = null;
            if (ActiveNode is not null)
            {
                viewedNode = ActiveNode;
            }
            else if (Guid.TryParse(viewedNodeSetting, out var restoredNodeId))
            {
                viewedNode = KnownNodes.FirstOrDefault(item => item.Id == restoredNodeId);
            }
            else
            {
                // Upgrade older installations using the node last seen by Identify, not a manual
                // offline selection or a profile/endpoint. No arbitrary cross-node browsing.
                var latest = nodes.OrderByDescending(node => node.LastSeenUtc).FirstOrDefault();
                viewedNode = KnownNodes.FirstOrDefault(item => item.Id == latest?.Id);
            }

            if (activeNode is not null)
                await _settings.SetAsync(LastConnectedNodeSettingKey, activeNode.Id.ToString("D"), linkedCancellation.Token);

            var version = ApplyViewedNode(viewedNode);
            if (viewedNode is not null)
            {
                await LoadViewedHistoryAsync(
                    viewedNode.Id,
                    version,
                    linkedCancellation.Token,
                    dispatchResult: false);
                Status = Navigation.Status;
            }
            await Task.WhenAll(_composerContexts.Select(context => context.InitializeAsync()));
        }
        catch (OperationCanceledException)
        {
            Status = "Загрузка локальной истории отменена";
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not load local message history.");
            ErrorMessage = "Не удалось прочитать локальную историю.";
            Status = "Ошибка локального хранилища";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public Task SelectViewedNodeAsync(
        KnownNodeListItem? node,
        CancellationToken cancellationToken = default)
    {
        if (node is not null && KnownNodes.All(item => item.Id != node.Id))
        {
            throw new ArgumentException("The selected node is not in the known-node list.", nameof(node));
        }

        return CanSelectViewedNode
            ? Track(SelectViewedNodeCoreAsync(node, cancellationToken))
            : Task.CompletedTask;
    }

    public Task SelectConversationAsync(
        ConversationListItem? conversation,
        CancellationToken cancellationToken = default)
    {
        return Track(SelectConversationCoreAsync(Navigation, conversation, cancellationToken));
    }

    public Task SelectNavigationTabAsync(
        MessengerNavigationTabItem tab,
        CancellationToken cancellationToken = default) =>
        Track(SelectNavigationTabCoreAsync(tab, cancellationToken));

    public void SetNarrowLayout(bool value) => Navigation.SetNarrowLayout(value);

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        if (Modal.IsOpen) await _dispatcher.InvokeAsync(() => Modal.Close(ModalCloseReason.Shutdown));
        if (_outgoingMessages is not null) _outgoingMessages.MessageCommitted -= OnOutgoingCommitted;
        if (_directoryUpdates is not null) _directoryUpdates.ContactRouteCommitted -= OnContactRouteCommitted;
        _supervisor.StateChanged -= OnSupervisorStateChanged;
        _commitNotifications.MessageCommitted -= OnMessageCommitted;
        Shell.PropertyChanged -= OnShellPropertyChanged;
        Chats.PropertyChanged -= OnNavigationPropertyChanged;
        _channelRepeats.Dispose();
        _privateResends.Dispose();
        _lifetimeCancellation.Cancel();
        RetryPreferencesSaveCommand.Cancel();
        RetryPreferencesSaveCommand.NotifyCanExecuteChanged();
        ConnectCommand.Cancel();
        DisconnectCommand.Cancel();
        _projectionRefreshSignal.Release();
        // Child shutdown starts touch bound UI state; retain the UI context between them.
        await Task.WhenAll(_composerContexts.Select(context => context.StopAsync()));
        await Chats.StopAsync();
        await Profiles.StopAsync();
        await Devices.StopAsync();

        Task[] pending;
        lock (_pendingLoadsGate)
        {
            pending =
            [
                .. _pendingLoads,
                .. new[]
                {
                    ConnectCommand.ExecutionTask,
                    DisconnectCommand.ExecutionTask,
                }.OfType<Task>(),
            ];
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    async Task IDesktopUiLifetime.ReportShutdownFailureAsync(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        await _dispatcher.InvokeAsync(
            () =>
            {
                ErrorMessage = "Не удалось сохранить все локальные данные, включая черновики. " +
                    "Исправьте проблему с локальным хранилищем и повторите закрытие окна.";
                Status = "Закрытие отменено: данные ещё не сохранены";
            },
            CancellationToken.None);
    }

    private Task Track(Task task)
    {
        lock (_pendingLoadsGate)
        {
            _pendingLoads.Add(task);
        }

        _ = task.ContinueWith(
            completedTask =>
            {
                lock (_pendingLoadsGate)
                {
                    _pendingLoads.Remove(completedTask);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            await _supervisor.ConnectNowAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not request a connection.");
            await SetErrorAsync("Не удалось начать подключение.");
        }
    }

    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            await _supervisor.DisconnectAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not request a disconnect.");
            await SetErrorAsync("Не удалось отключиться.");
        }
    }

    private async Task SelectNavigationTabCoreAsync(
        MessengerNavigationTabItem tab,
        CancellationToken cancellationToken)
    {
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            Shell.SelectSection(tab.Tab == MessengerNavigationTab.Channels ? ShellSection.PublicChats : ShellSection.PrivateChats);
            await Chats.SaveActiveSectionAsync(linkedCancellation.Token);
            Status = Navigation.Status;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not change the navigation tab.");
            ErrorMessage = "Не удалось открыть выбранный раздел.";
            Status = "Ошибка локального хранилища";
        }
    }

    private async Task SelectViewedNodeCoreAsync(
        KnownNodeListItem? node,
        CancellationToken cancellationToken)
    {
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            var version = ApplyViewedNode(node);
            await PersistViewedNodeAsync(node, linkedCancellation.Token);
            if (node is not null)
            {
                await LoadViewedHistoryAsync(node.Id, version, linkedCancellation.Token);
                Status = Navigation.Status;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not change the viewed node.");
            ErrorMessage = "Не удалось открыть или сохранить выбор ноды.";
            Status = "Ошибка локального хранилища";
        }
    }

    private void OnSupervisorStateChanged(object? sender, ConnectionSupervisorStateChangedEventArgs args)
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        var version = Interlocked.Increment(ref _connectionStateVersion);
        Track(ApplyConnectionSnapshotAsync(args.Current, version));
    }

    private async Task ApplyConnectionSnapshotAsync(ConnectionSupervisorSnapshot snapshot, long version)
    {
        try
        {
            var node = await ReadSnapshotNodeAsync(snapshot, _lifetimeCancellation.Token);
            Guid? followedNodeId = null;
            KnownNodeListItem? followedNode = null;
            long contextVersion = 0;
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (Volatile.Read(ref _stopped) != 0 ||
                        version != Volatile.Read(ref _connectionStateVersion))
                    {
                        return;
                    }

                    ApplyConnectionPresentation(snapshot, node);
                    if (ActiveNode is { } activeNode &&
                        ViewedNode?.Id != activeNode.Id)
                    {
                        followedNode = activeNode;
                        followedNodeId = activeNode.Id;
                        contextVersion = ApplyViewedNode(activeNode);
                    }
                },
                _lifetimeCancellation.Token);

            if (followedNodeId is { } nodeId)
            {
                await PersistViewedNodeAsync(
                    followedNode,
                    _lifetimeCancellation.Token);
                await LoadViewedHistoryAsync(nodeId, contextVersion, _lifetimeCancellation.Token);
            }

            if (node is not null && version == Volatile.Read(ref _connectionStateVersion) && ActiveNode?.Id == node.Id)
                await _settings.SetAsync(LastConnectedNodeSettingKey, node.Id.ToString("D"), _lifetimeCancellation.Token);

            if (snapshot.State == ConnectionSupervisorState.Online &&
                followedNodeId is null &&
                snapshot.NodeId == ViewedNode?.Id)
            {
                RequestProjectionRefresh();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not apply the active node context.");
            await SetErrorAsync("Не удалось прочитать сведения о подключённой ноде.");
        }
    }

    private async Task<NodeRecord?> ReadSnapshotNodeAsync(
        ConnectionSupervisorSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!SnapshotCanExposeNode(snapshot) || snapshot.NodeId is not { } nodeId)
        {
            return null;
        }

        return await _nodes.GetAsync(nodeId, cancellationToken);
    }

    private void ApplyConnectionPresentation(ConnectionSupervisorSnapshot snapshot, NodeRecord? node)
    {
        if (_connectionState != snapshot.State)
        {
            _connectionState = snapshot.State;
            OnPropertyChanged(nameof(CanSelectViewedNode));
        }

        (ConnectionStatus, ConnectionStatusDetail) = DescribeConnection(snapshot);
        ActiveProfileDisplayName = snapshot.ProfileId is { } profileId
            ? Profiles.AvailableProfiles.FirstOrDefault(item => item.Id == profileId)?.DisplayName
                ?? profileId.ToString("D")
            : "Не выбран";

        if (node is null)
        {
            ActiveNode = null;
        }
        else
        {
            ActiveNode = UpsertKnownNode(node);
        }
        OnPropertyChanged(nameof(Title));
        foreach (var context in _composerContexts) context.Refresh();
        Chats.Private.RouteReset.Invalidate();
    }

    private static bool SnapshotCanExposeNode(ConnectionSupervisorSnapshot snapshot) =>
        snapshot.State is ConnectionSupervisorState.Synchronizing or
            ConnectionSupervisorState.Online or
            ConnectionSupervisorState.NeedsAttention;

    private void OnMessageCommitted(object? sender, IncomingMessageCommitEvent args)
    {
        if (!args.Message.Inserted ||
            args.Message.NodeId != ViewedNode?.Id ||
            Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        _pendingCommittedMessages.Enqueue(args.Message);
        RequestProjectionRefresh();
    }

    private void RequestProjectionRefresh()
    {
        if (Volatile.Read(ref _stopped) == 0 &&
            Interlocked.Exchange(ref _projectionRefreshRequested, 1) == 0)
        {
            _projectionRefreshSignal.Release();
        }
    }

    private async Task ProcessProjectionRefreshesAsync()
    {
        try
        {
            while (true)
            {
                await _projectionRefreshSignal.WaitAsync(_lifetimeCancellation.Token);
                _lifetimeCancellation.Token.ThrowIfCancellationRequested();
                Interlocked.Exchange(ref _projectionRefreshRequested, 0);

                try
                {
                    await RefreshCommittedProjectionAsync(_lifetimeCancellation.Token);
                }
                catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Could not refresh history after a committed message.");
                    await SetErrorAsync("Не удалось обновить локальную историю.");
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
    }

    private void OnOutgoingCommitted(object? sender, OutgoingMessageCommit commit)
    {
        if (Volatile.Read(ref _stopped) != 0) return;
        _pendingOutgoing.Enqueue(commit);
        RequestProjectionRefresh();
    }

    private void OnContactRouteCommitted(object? sender, ContactRouteCommit commit)
    {
        if (Volatile.Read(ref _stopped) != 0 || ViewedNode?.Id != commit.NodeId) return;
        RequestProjectionRefresh();
    }

    private async Task ApplyHistoryClearAsync(HistoryClearResult result)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            foreach (var workspace in new[] { Chats.Public, Chats.Private })
                workspace.Navigation.InvalidateHistoryClear(result);
        }, _lifetimeCancellation.Token);
        await Chats.RefreshAsync(_lifetimeCancellation.Token);
        await Devices.RefreshAsync(_lifetimeCancellation.Token);
        foreach (var workspace in new[] { Chats.Public, Chats.Private })
            if (workspace.ViewedNode?.Id == result.NodeId && workspace.SelectedConversation?.Id == result.ConversationId)
                await workspace.Navigation.History.OpenAsync(result.NodeId, result.ConversationId, _lifetimeCancellation.Token);
    }

    private async Task RefreshCommittedProjectionAsync(CancellationToken cancellationToken)
    {
        await Chats.RefreshAsync(cancellationToken);
        await Devices.RefreshAsync(cancellationToken);
        while (_pendingCommittedMessages.TryDequeue(out var message))
        {
            await Chats.HandleCommittedMessageAsync(message, cancellationToken);
        }

        while (_pendingOutgoing.TryDequeue(out var outgoing))
            foreach (var workspace in new[] { Chats.Public, Chats.Private })
                await workspace.Navigation.History.HandleOutgoingCommitAsync(outgoing, cancellationToken);
        await _dispatcher.InvokeAsync(() => Status = Navigation.Status, cancellationToken);
    }

    private async Task LoadViewedHistoryAsync(
        Guid nodeId,
        long contextVersion,
        CancellationToken cancellationToken,
        bool dispatchResult = true)
    {
        if (contextVersion != Volatile.Read(ref _viewContextVersion) || ViewedNode?.Id != nodeId)
        {
            return;
        }

        var shellVersion = Volatile.Read(ref _shellRequestVersion);
        await Chats.LoadNodeAsync(nodeId, cancellationToken, dispatchResult);
        if (contextVersion == Volatile.Read(ref _viewContextVersion))
            await Devices.RefreshAsync(cancellationToken, dispatchResult);
        if (contextVersion == Volatile.Read(ref _viewContextVersion) && ViewedNode?.Id == nodeId && Shell.IsChatSelected &&
            shellVersion == Volatile.Read(ref _shellRequestVersion))
        {
            void SyncShell()
            {
                _syncingShell = true;
                try
                {
                    Shell.SelectSection(Navigation.SelectedTab.Tab switch
                    {
                        MessengerNavigationTab.Channels => ShellSection.PublicChats,
                        MessengerNavigationTab.Devices => ShellSection.Devices,
                        _ => ShellSection.PrivateChats,
                    });
                }
                finally { _syncingShell = false; }
            }
            if (dispatchResult)
                await _dispatcher.InvokeAsync(SyncShell, cancellationToken);
            else
                SyncShell();
        }
    }

    private void OnShellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(NavigationShellViewModel.SelectedItem) || _syncingShell || Volatile.Read(ref _stopped) != 0)
        {
            if (args.PropertyName == nameof(NavigationShellViewModel.IsChatSelected))
                OnPropertyChanged(nameof(IsChatWorkspaceVisible));
            return;
        }
        Interlocked.Increment(ref _shellRequestVersion);
        Chats.SelectSection(Shell.SelectedItem.Section);
        OnPropertyChanged(nameof(IsChatWorkspaceVisible));
        if (Shell.IsChatSelected) Track(PersistChatSectionAsync());
    }

    private void OnNavigationPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        OnPropertyChanged(nameof(Navigation));
        OnPropertyChanged(nameof(Conversations));
        OnPropertyChanged(nameof(Messages));
        OnPropertyChanged(nameof(SelectedConversation));
        OnPropertyChanged(nameof(IsChatWorkspaceVisible));
    }

    private async Task PersistChatSectionAsync()
    {
        try
        {
            await Chats.SaveActiveSectionAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger.LogError(error, "Could not save the chat section.");
            await SetErrorAsync("Не удалось сохранить выбранный раздел.");
        }
    }

    private async Task PersistViewedNodeAsync(
        KnownNodeListItem? node,
        CancellationToken cancellationToken)
    {
        await _viewSelectionPersistence.WaitAsync(cancellationToken);
        try
        {
            if (ViewedNode?.Id != node?.Id)
            {
                return;
            }

            await _settings.SetAsync(
                ViewedNodeSettingKey,
                node?.Id.ToString("D") ?? string.Empty,
                cancellationToken);
        }
        finally
        {
            _viewSelectionPersistence.Release();
        }
    }

    private long ApplyViewedNode(KnownNodeListItem? node)
    {
        var version = Interlocked.Increment(ref _viewContextVersion);
        ViewedNode = node;
        Devices.SetNode(node?.Id, node?.HeaderLabel);
        Chats.SetNode(node);
        ErrorMessage = null;
        Status = node is null ? "Нет истории подключённой ноды" : "Загрузка истории ноды…";
        return version;
    }

    private void ReplaceKnownNodes(IReadOnlyList<NodeRecord> nodes)
    {
        KnownNodes.Clear();
        foreach (var node in nodes)
        {
            KnownNodes.Add(new KnownNodeListItem(node));
        }
    }

    private KnownNodeListItem UpsertKnownNode(NodeRecord node)
    {
        var item = new KnownNodeListItem(node);
        var existing = KnownNodes.FirstOrDefault(candidate => candidate.Id == node.Id);
        if (existing is null)
        {
            KnownNodes.Insert(0, item);
        }
        else
        {
            KnownNodes[KnownNodes.IndexOf(existing)] = item;
        }

        if (ViewedNode?.Id == item.Id)
        {
            ViewedNode = item;
            Chats.UpdateNodeLabel(item);
            Devices.SetNode(item.Id, item.HeaderLabel);
        }

        return item;
    }

    private async Task SetErrorAsync(string message)
    {
        try
        {
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (Volatile.Read(ref _stopped) == 0)
                    {
                        ErrorMessage = message;
                    }
                },
                _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
    }

    private static (string Status, string? Detail) DescribeConnection(ConnectionSupervisorSnapshot snapshot)
    {
        var status = snapshot.State switch
        {
            ConnectionSupervisorState.Offline => "Не подключено",
            ConnectionSupervisorState.Connecting => "Подключение…",
            ConnectionSupervisorState.Identifying => "Идентификация…",
            ConnectionSupervisorState.Synchronizing => "Синхронизация…",
            ConnectionSupervisorState.Online => "Подключено",
            ConnectionSupervisorState.RetryWaiting => "Ожидание повтора",
            ConnectionSupervisorState.Disconnecting => "Отключение…",
            ConnectionSupervisorState.NeedsAttention => "Требуется внимание",
            _ => throw new ArgumentOutOfRangeException(nameof(snapshot)),
        };
        var detail = snapshot.Reason;
        if (snapshot.State == ConnectionSupervisorState.RetryWaiting && snapshot.NextAttemptUtc is { } retryAt)
        {
            var retry = $"Следующая попытка: {retryAt.ToLocalTime():g}";
            detail = string.IsNullOrWhiteSpace(detail) ? retry : $"{detail} {retry}";
        }

        return (status, detail);
    }

    private async Task SelectConversationCoreAsync(
        ConversationNavigationViewModel navigation,
        ConversationListItem? conversation,
        CancellationToken cancellationToken)
    {
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            await navigation.SelectConversationAsync(conversation, linkedCancellation.Token);
            Status = Navigation.Status;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not load a conversation.");
            ErrorMessage = "Не удалось прочитать сообщения выбранного диалога.";
            Status = "Ошибка локального хранилища";
        }
    }
}

public sealed record DesktopThemeOption(DesktopThemePreference Value, string Title);
public sealed record DesktopCloseBehaviorOption(DesktopCloseBehavior Value, string Title);

public sealed class KnownNodeListItem
{
    public KnownNodeListItem(NodeRecord node)
    {
        ArgumentNullException.ThrowIfNull(node);
        Id = node.Id;
        MentionName = string.IsNullOrWhiteSpace(node.LastName) ? null : node.LastName;
        Name = string.IsNullOrWhiteSpace(node.LastName) ? "Нода без имени" : node.LastName;
        PublicKeyHex = Convert.ToHexString(node.PublicKey).ToLowerInvariant();
        ShortPublicKey = PublicKeyHex.Length <= 12 ? PublicKeyHex : $"{PublicKeyHex[..12]}…";
        HeaderLabel = $"{Name} ({ShortPublicKey})";
        SelectorLabel = $"{Name} — {ShortPublicKey}";
    }

    public Guid Id { get; }
    public string? MentionName { get; }
    public string Name { get; }
    public string PublicKeyHex { get; }
    public string ShortPublicKey { get; }
    public string HeaderLabel { get; }
    public string SelectorLabel { get; }
}
