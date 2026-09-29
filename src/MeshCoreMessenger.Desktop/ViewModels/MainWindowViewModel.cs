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
    internal const string FollowActiveNodeSettingKey = "desktop.follow-active-node";

    private const int KnownNodePageSize = 500;
    private const int ConversationPageSize = 200;
    private const int MessagePageSize = 100;

    private readonly ILocalHistoryReader _history;
    private readonly INodeStore _nodes;
    private readonly ISettingsStore _settings;
    private readonly IConnectionSupervisor _supervisor;
    private readonly IMessageCommitNotifications _commitNotifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _projectionRefreshSignal = new(0);
    private readonly SemaphoreSlim _viewSelectionPersistence = new(1, 1);
    private readonly object _pendingLoadsGate = new();
    private readonly HashSet<Task> _pendingLoads = [];
    private readonly Task _projectionRefreshWorker;
    private KnownNodeListItem? _viewedNode;
    private KnownNodeListItem? _activeNode;
    private ConversationListItem? _selectedConversation;
    private string _status = "Загрузка локальной истории…";
    private string _connectionStatus;
    private string? _connectionStatusDetail;
    private string _activeProfileDisplayName = "Не выбран";
    private string? _errorMessage;
    private bool _isLoading;
    private bool _isFollowingActiveNode = true;
    private long _viewContextVersion;
    private long _connectionStateVersion;
    private Guid? _loadedConversationId;
    private int _projectionRefreshRequested;
    private int _stopped;

    public MainWindowViewModel(
        ILocalHistoryReader history,
        INodeStore nodes,
        ISettingsStore settings,
        ConnectionProfilesViewModel profiles,
        IConnectionSupervisor supervisor,
        IMessageCommitNotifications commitNotifications,
        IUiDispatcher dispatcher,
        ILogger<MainWindowViewModel> logger)
    {
        _history = history;
        _nodes = nodes;
        _settings = settings;
        Profiles = profiles;
        _supervisor = supervisor;
        _commitNotifications = commitNotifications;
        _dispatcher = dispatcher;
        _logger = logger;
        (_connectionStatus, _connectionStatusDetail) = DescribeConnection(supervisor.Snapshot);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync);
        FollowActiveNodeCommand = new AsyncRelayCommand(FollowActiveNodeAsync);
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
                OnPropertyChanged(nameof(CanFollowActiveNode));
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
                OnPropertyChanged(nameof(CanFollowActiveNode));
            }
        }
    }

    public bool HasViewedNode => ViewedNode is not null;
    public string? ViewedNodePublicKeyHex => ViewedNode?.PublicKeyHex;

    public bool IsFollowingActiveNode
    {
        get => _isFollowingActiveNode;
        private set
        {
            if (SetProperty(ref _isFollowingActiveNode, value))
            {
                OnPropertyChanged(nameof(CanFollowActiveNode));
            }
        }
    }

    public bool CanFollowActiveNode =>
        ActiveNode is not null &&
        (!IsFollowingActiveNode || ViewedNode?.Id != ActiveNode.Id);

    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand DisconnectCommand { get; }
    public IAsyncRelayCommand FollowActiveNodeCommand { get; }
    public ConnectionProfilesViewModel Profiles { get; }
    public ObservableCollection<KnownNodeListItem> KnownNodes { get; } = [];
    public ObservableCollection<ConversationListItem> Conversations { get; } = [];
    public ObservableCollection<HistoryMessageListItem> Messages { get; } = [];

    public ConversationListItem? SelectedConversation
    {
        get => _selectedConversation;
        private set => SetProperty(ref _selectedConversation, value);
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
            var followSetting = await _settings.GetAsync(FollowActiveNodeSettingKey, linkedCancellation.Token);

            ReplaceKnownNodes(nodes);
            IsFollowingActiveNode = !bool.TryParse(followSetting, out var follow) || follow;

            var snapshot = _supervisor.Snapshot;
            var activeNode = await ReadSnapshotNodeAsync(snapshot, linkedCancellation.Token);
            ApplyConnectionPresentation(snapshot, activeNode);

            KnownNodeListItem? viewedNode = null;
            if (IsFollowingActiveNode && snapshot.State == ConnectionSupervisorState.Online)
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

        return Track(SelectViewedNodeCoreAsync(node, followActiveNode: false, cancellationToken));
    }

    public Task SelectConversationAsync(
        ConversationListItem? conversation,
        CancellationToken cancellationToken = default)
    {
        if (conversation is not null && conversation.NodeId != ViewedNode?.Id)
        {
            throw new ArgumentException("The conversation does not belong to the viewed node.", nameof(conversation));
        }

        if (conversation is not null &&
            SelectedConversation?.Id == conversation.Id &&
            _loadedConversationId == conversation.Id)
        {
            return Task.CompletedTask;
        }

        return Track(SelectConversationCoreAsync(conversation, cancellationToken));
    }

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
        FollowActiveNodeCommand.Cancel();
        _projectionRefreshSignal.Release();
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
                    FollowActiveNodeCommand.ExecutionTask,
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

    private Task FollowActiveNodeAsync(CancellationToken cancellationToken) =>
        SelectViewedNodeCoreAsync(ActiveNode, followActiveNode: true, cancellationToken);

    private async Task SelectViewedNodeCoreAsync(
        KnownNodeListItem? node,
        bool followActiveNode,
        CancellationToken cancellationToken)
    {
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            IsFollowingActiveNode = followActiveNode;
            var version = ApplyViewedNode(node);
            await PersistViewedNodeAsync(
                node,
                followActiveNode,
                linkedCancellation.Token);
            if (node is not null)
            {
                await LoadViewedHistoryAsync(node.Id, version, linkedCancellation.Token);
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
                    if (snapshot.State == ConnectionSupervisorState.Online &&
                        IsFollowingActiveNode &&
                        ActiveNode is { } activeNode &&
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
                    followActiveNode: true,
                    _lifetimeCancellation.Token);
                await LoadViewedHistoryAsync(nodeId, contextVersion, _lifetimeCancellation.Token);
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
        Guid? nodeId = null;
        Guid? selectedId = null;
        long contextVersion = 0;
        await _dispatcher.InvokeAsync(
            () =>
            {
                nodeId = ViewedNode?.Id;
                selectedId = SelectedConversation?.Id;
                contextVersion = Volatile.Read(ref _viewContextVersion);
            },
            cancellationToken);

        if (nodeId is null)
        {
            return;
        }

        var summaries = await _history.GetConversationsAsync(
            nodeId.Value,
            ConversationPageSize,
            cancellationToken);
        var targetId = selectedId is { } id && summaries.Any(summary => summary.Id == id)
            ? id
            : summaries.FirstOrDefault()?.Id;
        var messages = targetId is { } conversationId
            ? await _history.GetMessagesAsync(
                nodeId.Value,
                conversationId,
                beforeLocalSequence: null,
                MessagePageSize,
                cancellationToken)
            : [];

        await _dispatcher.InvokeAsync(
            () =>
            {
                if (Volatile.Read(ref _stopped) != 0)
                {
                    return;
                }

                if (contextVersion != Volatile.Read(ref _viewContextVersion) ||
                    ViewedNode?.Id != nodeId)
                {
                    RequestProjectionRefresh();
                    return;
                }

                ApplyProjection(summaries, targetId, messages);
            },
            cancellationToken);
    }

    private async Task LoadViewedHistoryAsync(
        Guid nodeId,
        long contextVersion,
        CancellationToken cancellationToken,
        bool dispatchResult = true)
    {
        var summaries = await _history.GetConversationsAsync(
            nodeId,
            ConversationPageSize,
            cancellationToken);
        var targetId = summaries.FirstOrDefault()?.Id;
        var messages = targetId is { } conversationId
            ? await _history.GetMessagesAsync(
                nodeId,
                conversationId,
                beforeLocalSequence: null,
                MessagePageSize,
                cancellationToken)
            : [];

        void ApplyIfCurrent()
        {
            if (Volatile.Read(ref _stopped) != 0 ||
                contextVersion != Volatile.Read(ref _viewContextVersion) ||
                ViewedNode?.Id != nodeId)
            {
                return;
            }

            ApplyProjection(summaries, targetId, messages);
        }

        if (dispatchResult)
        {
            await _dispatcher.InvokeAsync(ApplyIfCurrent, cancellationToken);
        }
        else
        {
            ApplyIfCurrent();
        }
    }

    private void ApplyProjection(
        IReadOnlyList<ConversationSummary> summaries,
        Guid? targetId,
        IReadOnlyList<HistoryMessage> messages)
    {
        if (summaries.Any(summary => summary.NodeId != ViewedNode?.Id))
        {
            throw new InvalidOperationException("The history projection crossed a node boundary.");
        }

        Conversations.Clear();
        foreach (var summary in summaries)
        {
            Conversations.Add(new ConversationListItem(summary));
        }

        SelectedConversation = targetId is { } currentId
            ? Conversations.First(item => item.Id == currentId)
            : null;
        Messages.Clear();
        foreach (var message in messages)
        {
            Messages.Add(new HistoryMessageListItem(message));
        }

        _loadedConversationId = targetId;
        Status = Conversations.Count == 0
            ? "История выбранной ноды пуста"
            : $"Загружено диалогов: {Conversations.Count}";
        ErrorMessage = null;
    }

    private async Task PersistViewedNodeAsync(
        KnownNodeListItem? node,
        bool followActiveNode,
        CancellationToken cancellationToken)
    {
        await _viewSelectionPersistence.WaitAsync(cancellationToken);
        try
        {
            if (ViewedNode?.Id != node?.Id || IsFollowingActiveNode != followActiveNode)
            {
                return;
            }

            await _settings.SetAsync(
                FollowActiveNodeSettingKey,
                followActiveNode.ToString(),
                cancellationToken);
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
        SelectedConversation = null;
        _loadedConversationId = null;
        Conversations.Clear();
        Messages.Clear();
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
        var nodeId = ViewedNode?.Id;
        var version = Interlocked.Increment(ref _viewContextVersion);
        SelectedConversation = conversation;
        _loadedConversationId = null;
        Messages.Clear();
        ErrorMessage = null;
        if (conversation is null || nodeId is null)
        {
            return;
        }

        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            var messages = await _history.GetMessagesAsync(
                nodeId.Value,
                conversation.Id,
                beforeLocalSequence: null,
                MessagePageSize,
                linkedCancellation.Token);

            if (version != Volatile.Read(ref _viewContextVersion) || ViewedNode?.Id != nodeId)
            {
                return;
            }

            Messages.Clear();
            foreach (var message in messages)
            {
                Messages.Add(new HistoryMessageListItem(message));
            }

            _loadedConversationId = conversation.Id;
            Status = $"Загружено диалогов: {Conversations.Count}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not load conversation {ConversationId}.", conversation.Id);
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

public sealed class ConversationListItem
{
    public ConversationListItem(ConversationSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Id = summary.Id;
        NodeId = summary.NodeId;
        Kind = summary.Kind;
        Title = string.IsNullOrWhiteSpace(summary.Title)
            ? summary.Kind switch
            {
                ConversationKind.Channel or ConversationKind.UnknownChannel => "Канал без названия",
                ConversationKind.Contact or ConversationKind.UnknownContact => "Неизвестный контакт",
                _ => "Диалог без названия",
            }
            : summary.Title;
        Preview = summary.LastMessageKind switch
        {
            StoredMessageKind.Binary => "Двоичное сообщение",
            StoredMessageKind.Text when !string.IsNullOrEmpty(summary.LastMessageText) => summary.LastMessageText,
            _ => "Нет сообщений",
        };
        ActivityTime = (summary.LastMessageUtc ?? summary.UpdatedUtc).ToLocalTime().ToString("g");
    }

    public Guid Id { get; }
    public Guid NodeId { get; }
    public ConversationKind Kind { get; }
    public string Title { get; }
    public string Preview { get; }
    public string ActivityTime { get; }
}

public sealed class HistoryMessageListItem
{
    public HistoryMessageListItem(HistoryMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        LocalSequence = message.LocalSequence;
        Direction = message.Direction == MessageDirection.Outgoing ? "Вы" : "Входящее";
        Body = message.MessageKind == StoredMessageKind.Binary
            ? "Двоичное сообщение"
            : message.Text ?? string.Empty;
        ReceivedTime = message.ReceivedUtc.ToLocalTime().ToString("g");
    }

    public long LocalSequence { get; }
    public string Direction { get; }
    public string Body { get; }
    public string ReceivedTime { get; }
}
