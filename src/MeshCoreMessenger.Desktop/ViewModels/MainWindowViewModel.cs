using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;

namespace MeshCoreMessenger.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private const int ConversationPageSize = 200;
    private const int MessagePageSize = 100;

    private readonly ILocalHistoryReader _history;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _pendingLoadsGate = new();
    private readonly HashSet<Task> _pendingLoads = [];
    private ConversationListItem? _selectedConversation;
    private string _status = "Загрузка локальной истории…";
    private string? _errorMessage;
    private bool _isLoading;
    private long _selectionVersion;
    private Guid? _loadedConversationId;
    private int _stopped;

    public MainWindowViewModel(
        ILocalHistoryReader history,
        ConnectionProfilesViewModel profiles,
        ILogger<MainWindowViewModel> logger)
    {
        _history = history;
        Profiles = profiles;
        _logger = logger;
    }

    public string Title => AppInformation.ProductName;
    public string ConnectionStatus => "Не подключено";
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

        var task = SelectConversationCoreAsync(conversation, cancellationToken);
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

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        await Profiles.StopAsync().ConfigureAwait(false);
        Task[] pending;
        lock (_pendingLoadsGate)
        {
            pending = [.. _pendingLoads];
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _lifetimeCancellation.Dispose();
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
