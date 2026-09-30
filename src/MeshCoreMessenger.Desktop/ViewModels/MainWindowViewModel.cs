using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;

namespace MeshCoreMessenger.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDesktopUiLifetime
{
    internal const string ViewedNodeSettingKey = "desktop.viewed-node-id";

    private const int KnownNodePageSize = 500;
    private readonly INodeStore _nodes;
    private readonly ISettingsStore _settings;
    private readonly IConnectionSupervisor _supervisor;
    private readonly IMessageCommitNotifications _commitNotifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<MainWindowViewModel> _logger;
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
    private bool _isLoading;
    private ConnectionSupervisorState _connectionState;
    private bool _isConnectionSettingsOpen;
    private long _viewContextVersion;
    private long _connectionStateVersion;
    private int _projectionRefreshRequested;
    private int _stopped;

    public MainWindowViewModel(
        IConversationDirectoryReader directory,
        ILocalHistoryReader history,
        IConversationReadStateStore readStates,
        IDurableReadStateWrites readWrites,
        INodeStore nodes,
        ISettingsStore settings,
        ConnectionProfilesViewModel profiles,
        IConnectionSupervisor supervisor,
        IMessageCommitNotifications commitNotifications,
        IUiDispatcher dispatcher,
        ILogger<MainWindowViewModel> logger)
    {
        _nodes = nodes;
        _settings = settings;
        Profiles = profiles;
        _supervisor = supervisor;
        _commitNotifications = commitNotifications;
        _dispatcher = dispatcher;
        _logger = logger;
        _connectionState = supervisor.Snapshot.State;
        Navigation = new ConversationNavigationViewModel(
            directory,
            history,
            readStates,
            readWrites,
            settings,
            dispatcher,
            logger);
        (_connectionStatus, _connectionStatusDetail) = DescribeConnection(supervisor.Snapshot);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync);
        ToggleConnectionSettingsCommand = new RelayCommand(
            () => IsConnectionSettingsOpen = !IsConnectionSettingsOpen);
        CloseConnectionSettingsCommand = new RelayCommand(() => IsConnectionSettingsOpen = false);
        _supervisor.StateChanged += OnSupervisorStateChanged;
        _commitNotifications.MessageCommitted += OnMessageCommitted;
        _projectionRefreshWorker = Track(ProcessProjectionRefreshesAsync());
    }

    public string Title => AppInformation.ProductName;

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

    public bool CanSelectViewedNode => _connectionState == ConnectionSupervisorState.Offline;

    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand DisconnectCommand { get; }
    public IRelayCommand ToggleConnectionSettingsCommand { get; }
    public IRelayCommand CloseConnectionSettingsCommand { get; }
    public ConnectionProfilesViewModel Profiles { get; }
    public ConversationNavigationViewModel Navigation { get; }
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
            await Profiles.LoadAsync(linkedCancellation.Token);
            var nodes = await _nodes.GetAllAsync(KnownNodePageSize, linkedCancellation.Token);
            var viewedNodeSetting = await _settings.GetAsync(ViewedNodeSettingKey, linkedCancellation.Token);

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
        return Track(SelectConversationCoreAsync(conversation, cancellationToken));
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

        _supervisor.StateChanged -= OnSupervisorStateChanged;
        _commitNotifications.MessageCommitted -= OnMessageCommitted;
        _lifetimeCancellation.Cancel();
        ConnectCommand.Cancel();
        DisconnectCommand.Cancel();
        _projectionRefreshSignal.Release();
        await Navigation.StopAsync().ConfigureAwait(false);
        await Profiles.StopAsync().ConfigureAwait(false);

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
                ErrorMessage = "Не удалось сохранить все входящие сообщения. " +
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
            await Navigation.SelectTabAsync(tab, linkedCancellation.Token);
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
            return;
        }

        ActiveNode = UpsertKnownNode(node);
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

    private async Task RefreshCommittedProjectionAsync(CancellationToken cancellationToken)
    {
        await Navigation.RefreshAsync(cancellationToken);
        while (_pendingCommittedMessages.TryDequeue(out var message))
        {
            await Navigation.HandleCommittedMessageAsync(message, cancellationToken);
        }

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

        await Navigation.LoadNodeAsync(nodeId, cancellationToken, dispatchResult);
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
        Navigation.ClearNode();
        ErrorMessage = null;
        Status = node is null ? "Выберите ноду для просмотра истории" : "Загрузка истории ноды…";
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
        ConversationListItem? conversation,
        CancellationToken cancellationToken)
    {
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            await Navigation.SelectConversationAsync(conversation, linkedCancellation.Token);
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

public sealed class KnownNodeListItem
{
    public KnownNodeListItem(NodeRecord node)
    {
        ArgumentNullException.ThrowIfNull(node);
        Id = node.Id;
        Name = string.IsNullOrWhiteSpace(node.LastName) ? "Нода без имени" : node.LastName;
        PublicKeyHex = Convert.ToHexString(node.PublicKey).ToLowerInvariant();
        ShortPublicKey = PublicKeyHex.Length <= 12 ? PublicKeyHex : $"{PublicKeyHex[..12]}…";
        HeaderLabel = $"{Name} ({ShortPublicKey})";
        SelectorLabel = $"{Name} — {ShortPublicKey}";
    }

    public Guid Id { get; }
    public string Name { get; }
    public string PublicKeyHex { get; }
    public string ShortPublicKey { get; }
    public string HeaderLabel { get; }
    public string SelectorLabel { get; }
}
