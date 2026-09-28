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

public sealed class MainWindowViewModel : ObservableObject
{
    private const int ConversationPageSize = 200;
    private const int MessagePageSize = 100;

    private readonly ILocalHistoryReader _history;
    private readonly IConnectionSupervisor _supervisor;
    private readonly IMessageCommitNotifications _commitNotifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _projectionRefreshSignal = new(0);
    private readonly object _pendingLoadsGate = new();
    private readonly HashSet<Task> _pendingLoads = [];
    private readonly Task _projectionRefreshWorker;
    private ConversationListItem? _selectedConversation;
    private string _status = "Загрузка локальной истории…";
    private string _connectionStatus;
    private string? _connectionStatusDetail;
    private string? _errorMessage;
    private bool _isLoading;
    private long _selectionVersion;
    private long _connectionStateVersion;
    private Guid? _loadedConversationId;
    private int _projectionRefreshRequested;
    private int _stopped;

    public MainWindowViewModel(
        ILocalHistoryReader history,
        ConnectionProfilesViewModel profiles,
        IConnectionSupervisor supervisor,
        IMessageCommitNotifications commitNotifications,
        IUiDispatcher dispatcher,
        ILogger<MainWindowViewModel> logger)
    {
        _history = history;
        Profiles = profiles;
        _supervisor = supervisor;
        _commitNotifications = commitNotifications;
        _dispatcher = dispatcher;
        _logger = logger;
        (_connectionStatus, _connectionStatusDetail) = DescribeConnection(supervisor.Snapshot);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync);
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
    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand DisconnectCommand { get; }
    public ConnectionProfilesViewModel Profiles { get; }
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
            var summaries = await _history.GetConversationsAsync(
                ConversationPageSize,
                linkedCancellation.Token);

            Conversations.Clear();
            foreach (var summary in summaries)
            {
                Conversations.Add(new ConversationListItem(summary));
            }

            if (Conversations.Count == 0)
            {
                SelectedConversation = null;
                Messages.Clear();
                Status = "Локальная история пуста";
                return;
            }

            await SelectConversationAsync(Conversations[0], linkedCancellation.Token);
            if (!HasError)
            {
                Status = $"Загружено диалогов: {Conversations.Count}";
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

    public Task SelectConversationAsync(
        ConversationListItem? conversation,
        CancellationToken cancellationToken = default)
    {
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
        _projectionRefreshSignal.Release();
        await Profiles.StopAsync().ConfigureAwait(false);

        Task[] pending;
        lock (_pendingLoadsGate)
        {
            pending =
            [
                .. _pendingLoads,
                .. new[] { ConnectCommand.ExecutionTask, DisconnectCommand.ExecutionTask }.OfType<Task>(),
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
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (Volatile.Read(ref _stopped) != 0 ||
                        version != Volatile.Read(ref _connectionStateVersion))
                    {
                        return;
                    }

                    (ConnectionStatus, ConnectionStatusDetail) = DescribeConnection(snapshot);
                },
                _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
    }

    private void OnMessageCommitted(object? sender, IncomingMessageCommitEvent args)
    {
        if (!args.Message.Inserted || Volatile.Read(ref _stopped) != 0)
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
        Guid? selectedId = null;
        long selectionVersion = 0;
        await _dispatcher.InvokeAsync(
            () =>
            {
                selectedId = SelectedConversation?.Id;
                selectionVersion = Volatile.Read(ref _selectionVersion);
            },
            cancellationToken);

        var summaries = await _history.GetConversationsAsync(ConversationPageSize, cancellationToken);
        var targetId = selectedId is { } id && summaries.Any(summary => summary.Id == id)
            ? id
            : summaries.FirstOrDefault()?.Id;
        var messages = targetId is { } conversationId
            ? await _history.GetMessagesAsync(
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

                if (selectionVersion != Volatile.Read(ref _selectionVersion))
                {
                    RequestProjectionRefresh();
                    return;
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
                    ? "Локальная история пуста"
                    : $"Загружено диалогов: {Conversations.Count}";
                ErrorMessage = null;
            },
            cancellationToken);
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
        var version = Interlocked.Increment(ref _selectionVersion);
        SelectedConversation = conversation;
        _loadedConversationId = null;
        Messages.Clear();
        ErrorMessage = null;
        if (conversation is null)
        {
            return;
        }

        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            var messages = await _history.GetMessagesAsync(
                conversation.Id,
                beforeLocalSequence: null,
                MessagePageSize,
                linkedCancellation.Token);

            if (version != Volatile.Read(ref _selectionVersion))
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
