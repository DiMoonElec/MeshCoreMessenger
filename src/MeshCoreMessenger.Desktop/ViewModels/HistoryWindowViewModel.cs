using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;

namespace MeshCoreMessenger.Desktop.ViewModels;

public enum HistoryScrollIntent { ToMessage, ToEnd, PreserveViewport }

public sealed class HistoryScrollRequestEventArgs(HistoryScrollIntent intent, long? anchorSequence = null) : EventArgs
{
    public HistoryScrollIntent Intent { get; } = intent;
    public long? AnchorSequence { get; } = anchorSequence;
    public bool ScrollToEnd => Intent == HistoryScrollIntent.ToEnd;
}

/// <summary>Owns one bounded, node-scoped message viewport.</summary>
public sealed class HistoryWindowViewModel : ObservableObject
{
    internal const int PageSize = 100;
    internal const int MaximumMessages = 500;
    internal const int SearchPageSize = 20;

    private readonly ILocalHistoryReader _history;
    private readonly IConversationReadStateStore _readStates;
    private readonly IDurableReadStateWrites _readWrites;
    private readonly IUiDispatcher _dispatcher;
    private readonly ISearchDelay _searchDelay;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly HashSet<Guid> _handledCommitIds = [];
    private readonly object _readTasksGate = new();
    private readonly HashSet<Task> _readTasks = [];
    private readonly object _searchTasksGate = new();
    private readonly HashSet<Task> _searchTasks = [];
    private Guid? _nodeId;
    private Guid? _conversationId;
    private HistoryMessagePosition? _firstPosition;
    private HistoryMessagePosition? _lastPosition;
    private bool _hasEarlier;
    private bool _hasLater;
    private bool _isAtLatest;
    private bool _isWindowActive;
    private long? _firstVisibleSequence;
    private long? _lastVisibleSequence;
    private int _pendingNewMessageCount;
    private long _lastReadSequence;
    private long _lastRequestedReadSequence;
    private long _observedReadThroughSequence;
    private long _unreadCount;
    private HistoryMessagePosition? _firstUnreadPosition;
    private string? _readErrorMessage;
    private string _searchText = string.Empty;
    private bool _isSearching;
    private string? _searchErrorMessage;
    private HistoryMessagePosition? _searchCursor;
    private long? _highlightedSearchSequence;
    private CancellationTokenSource? _searchCancellation;
    private long _searchRevision;
    private long _contextVersion;
    private int _stopped;

    public HistoryWindowViewModel(
        ILocalHistoryReader history,
        IConversationReadStateStore readStates,
        IDurableReadStateWrites readWrites,
        IUiDispatcher dispatcher,
        ISearchDelay searchDelay,
        ILogger logger)
    {
        _history = history;
        _readStates = readStates;
        _readWrites = readWrites;
        _dispatcher = dispatcher;
        _searchDelay = searchDelay;
        _logger = logger;
        LoadOlderCommand = new AsyncRelayCommand(LoadOlderAsync, () => CanLoadOlder);
        LoadNewerCommand = new AsyncRelayCommand(LoadNewerAsync, () => CanLoadNewer);
        JumpToLatestCommand = new AsyncRelayCommand(JumpToLatestAsync, () => HasConversation);
        JumpToFirstUnreadCommand = new AsyncRelayCommand(
            JumpToFirstUnreadAsync,
            () => FirstUnreadPosition is not null);
        LoadMoreSearchResultsCommand = new AsyncRelayCommand(
            LoadMoreSearchResultsAsync,
            () => CanLoadMoreSearchResults);
    }

    public event EventHandler<HistoryScrollRequestEventArgs>? ScrollRequested;
    /// <summary>UI must capture its anchor and suppress intermediate viewport reports before mutations.</summary>
    public event EventHandler? ViewportChanging;
    public event Action<ConversationReadState>? ReadStateChanged;

    public ObservableCollection<HistoryMessageListItem> Messages { get; } = [];
    public ObservableCollection<HistorySearchResultListItem> SearchResults { get; } = [];
    public IAsyncRelayCommand LoadOlderCommand { get; }
    public IAsyncRelayCommand LoadNewerCommand { get; }
    public IAsyncRelayCommand JumpToLatestCommand { get; }
    public IAsyncRelayCommand JumpToFirstUnreadCommand { get; }
    public IAsyncRelayCommand LoadMoreSearchResultsCommand { get; }
    public bool HasConversation => _conversationId is not null;
    public bool HasMessages => Messages.Count > 0;
    public bool HasEmptyHistory => HasConversation && !HasMessages;
    public bool CanLoadOlder => HasConversation && _hasEarlier;
    public bool CanLoadNewer => HasConversation && _hasLater;
    public bool IsAtLatest => _isAtLatest;
    public bool IsWindowActive => _isWindowActive;
    public long? FirstVisibleSequence => _firstVisibleSequence;
    public long? LastVisibleSequence => _lastVisibleSequence;
    public int PendingNewMessageCount => _pendingNewMessageCount;
    public bool HasPendingNewMessages => PendingNewMessageCount > 0;
    public long UnreadCount => _unreadCount;
    public bool HasUnreadMessages => UnreadCount > 0;
    public string UnreadLabel => UnreadCount == 1
        ? "1 непрочитанное"
        : $"{UnreadCount} непрочитанных";
    public HistoryMessagePosition? FirstUnreadPosition => _firstUnreadPosition;
    public string? ReadErrorMessage => _readErrorMessage;
    public bool HasReadError => !string.IsNullOrWhiteSpace(ReadErrorMessage);
    public string PendingNewMessagesLabel => PendingNewMessageCount == 1
        ? "1 новое сообщение"
        : $"{PendingNewMessageCount} новых сообщений";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                QueueSearch();
                RaiseSearchProperties();
            }
        }
    }
    public bool IsSearchActive => SearchText.Length > 0;
    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (SetProperty(ref _isSearching, value))
            {
                RaiseSearchProperties();
            }
        }
    }
    public bool HasSearchResults => SearchResults.Count > 0;
    public bool CanLoadMoreSearchResults => _searchCursor is not null && !IsSearching;
    public string SearchStatus => _searchErrorMessage ??
        (IsSearching
            ? "Поиск…"
            : IsSearchActive
                ? $"Совпадений загружено: {SearchResults.Count}"
                : string.Empty);
    public bool HasSearchStatus => !string.IsNullOrEmpty(SearchStatus);

    private readonly Dictionary<(Guid NodeId, Guid ConversationId), long> _clearedThrough = [];

    internal void InvalidateHistoryClear(HistoryClearResult result)
    {
        var key = (result.NodeId, result.ConversationId);
        _clearedThrough[key] = Math.Max(_clearedThrough.GetValueOrDefault(key), result.CutoffSequence);
        if (_nodeId == result.NodeId && _conversationId == result.ConversationId) Clear();
    }

    public void Clear()
    {
        ResetSearch();
        Interlocked.Increment(ref _contextVersion);
        _nodeId = null;
        _conversationId = null;
        _handledCommitIds.Clear();
        ApplyEmpty();
    }

    public async Task OpenAsync(
        Guid nodeId,
        Guid? conversationId,
        CancellationToken cancellationToken = default,
        bool dispatchResult = true)
    {
        var version = Interlocked.Increment(ref _contextVersion);
        _nodeId = nodeId;
        _conversationId = conversationId;
        _handledCommitIds.Clear();
        _lastReadSequence = 0;
        _lastRequestedReadSequence = 0;
        _observedReadThroughSequence = 0;
        _unreadCount = 0;
        _firstUnreadPosition = null;
        using var linked = CreateLinkedCancellation(cancellationToken);
        // A committed first message can materialize a conversation and reopen it
        // from the projection worker. Resetting search also notifies bound commands.
        await ApplyAsync(() =>
        {
            if (IsCurrent(nodeId, conversationId, version)) ResetSearch();
        }, dispatchResult, linked.Token);
        if (!IsCurrent(nodeId, conversationId, version)) return;
        var pageTask = conversationId is { } id
            ? _history.GetMessagesBeforeAsync(nodeId, id, null, PageSize, linked.Token)
            : Task.FromResult(EmptyPage);
        var readStateTask = conversationId is { } readConversationId
            ? _readStates.GetAsync(nodeId, readConversationId, linked.Token)
            : null;
        if (readStateTask is not null)
        {
            await Task.WhenAll(pageTask, readStateTask);
        }
        var page = await pageTask;
        var readState = readStateTask is null ? null : await readStateTask;
        await ApplyAsync(
            () =>
            {
                if (!IsCurrent(nodeId, conversationId, version))
                {
                    return;
                }

                ApplyReadState(readState);
                ApplyReplacement(page, requestScrollToEnd: page.Items.Count > 0);
            },
            dispatchResult,
            linked.Token);
    }

    public Task LoadOlderAsync(CancellationToken cancellationToken = default) =>
        LoadRelativeAsync(before: true, autoScrollToEnd: false, cancellationToken);

    public Task LoadNewerAsync(CancellationToken cancellationToken = default) =>
        LoadRelativeAsync(before: false, autoScrollToEnd: false, cancellationToken);

    public async Task JumpToLatestAsync(CancellationToken cancellationToken = default)
    {
        var nodeId = _nodeId;
        var conversationId = _conversationId;
        if (nodeId is null || conversationId is null)
        {
            return;
        }

        var version = Volatile.Read(ref _contextVersion);
        using var linked = CreateLinkedCancellation(cancellationToken);
        await _loadGate.WaitAsync(linked.Token);
        try
        {
            ClearSearchHighlight();
            var page = await _history.GetMessagesBeforeAsync(
                nodeId.Value,
                conversationId.Value,
                null,
                PageSize,
                linked.Token);
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (IsCurrent(nodeId.Value, conversationId, version))
                    {
                        ApplyReplacement(page, requestScrollToEnd: true);
                    }
                },
                linked.Token);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async Task JumpToFirstUnreadAsync(CancellationToken cancellationToken = default)
    {
        var position = FirstUnreadPosition;
        if (position is null)
        {
            return;
        }

        var version = Volatile.Read(ref _contextVersion);
        using var linked = CreateLinkedCancellation(cancellationToken);
        await _loadGate.WaitAsync(linked.Token);
        try
        {
            ClearSearchHighlight();
            var page = await _history.GetMessagesAroundAsync(
                position,
                beforeLimit: 20,
                afterLimit: PageSize - 21,
                linked.Token);
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (IsCurrent(position.NodeId, position.ConversationId, version) &&
                        FirstUnreadPosition?.MessageId == position.MessageId)
                    {
                        ApplyReplacement(page, requestScrollToEnd: false);
                        ScrollRequested?.Invoke(
                            this,
                            new HistoryScrollRequestEventArgs(HistoryScrollIntent.ToMessage, position.LocalSequence));
                    }
                },
                linked.Token);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async Task HandleCommittedMessageAsync(
        StoredIncomingMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_clearedThrough.TryGetValue((message.NodeId, message.ConversationId), out var cutoff) && message.LocalSequence <= cutoff) return;
        if (!message.Inserted)
        {
            return;
        }

        if (message.NodeId != _nodeId || message.ConversationId != _conversationId)
        {
            return;
        }

        var version = Volatile.Read(ref _contextVersion);
        var readState = await _readStates.GetAsync(
            message.NodeId,
            message.ConversationId,
            cancellationToken);
        var shouldLoad = false;
        await _dispatcher.InvokeAsync(
            () =>
            {
                if (!IsCurrent(message.NodeId, message.ConversationId, version))
                {
                    return;
                }

                ApplyReadState(readState, clearError: false);
                if (!_handledCommitIds.Add(message.MessageId) ||
                    Messages.Any(item => item.Id == message.MessageId))
                {
                    return;
                }

                if (IsAtLatest)
                {
                    shouldLoad = true;
                }
                else
                {
                    _hasLater = true;
                    PendingNewMessageCountCore++;
                    RaiseStateProperties();
                }
            },
            cancellationToken);
        if (!shouldLoad)
        {
            return;
        }

        if (_lastPosition is null)
        {
            await JumpToLatestAsync(cancellationToken);
            return;
        }

        await LoadRelativeAsync(before: false, autoScrollToEnd: true, cancellationToken);
    }

    public async Task HandleOutgoingCommitAsync(OutgoingMessageCommit commit, CancellationToken cancellationToken = default)
    {
        if (commit.NodeId != _nodeId || commit.ConversationId != _conversationId) return;
        var version = Volatile.Read(ref _contextVersion);
        var shouldLoad = false;
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            var position = await _history.GetMessagePositionAsync(commit.NodeId, commit.ConversationId, commit.MessageId, cancellationToken);
            if (position is null) return;
            var page = await _history.GetMessagesAroundAsync(position, 0, 0, cancellationToken);
            var message = page.Items.SingleOrDefault(m => m.Id == commit.MessageId);
            if (message is null) return;
            await _dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrent(commit.NodeId, commit.ConversationId, version)) return;
                var existing = Messages.FirstOrDefault(m => m.Id == commit.MessageId);
                if (existing is not null)
                {
                    ViewportChanging?.Invoke(this, EventArgs.Empty);
                    existing.Presentation = new HistoryMessageListItem(message).Presentation;
                    ScrollRequested?.Invoke(this, new(HistoryScrollIntent.PreserveViewport, _firstVisibleSequence));
                    return;
                }
                if (!commit.Inserted || !_handledCommitIds.Add(commit.MessageId)) return;
                if (IsAtLatest || Messages.Count == 0) shouldLoad = true;
                else
                {
                    _hasLater = true;
                    RaiseStateProperties();
                }
            }, cancellationToken);
        }
        finally { _loadGate.Release(); }
        if (!shouldLoad || !IsCurrent(commit.NodeId, commit.ConversationId, version)) return;
        // Read the complete bounded interval, including incoming commits interleaved with this send.
        if (_lastPosition is null) await JumpToLatestAsync(cancellationToken);
        else await LoadRelativeAsync(before: false, autoScrollToEnd: true, cancellationToken);
    }

    /// <summary>Reports what is actually visible and conservatively advances read state.</summary>
    public void ReportVisibleRange(
        long? firstSequence,
        long? lastSequence,
        bool isWindowActive,
        bool isAtVisualEnd)
    {
        _firstVisibleSequence = firstSequence;
        _lastVisibleSequence = lastSequence;
        _isWindowActive = isWindowActive;
        _isAtLatest = isAtVisualEnd && !_hasLater;
        if (_isAtLatest)
        {
            PendingNewMessageCountCore = 0;
        }

        OnPropertyChanged(nameof(FirstVisibleSequence));
        OnPropertyChanged(nameof(LastVisibleSequence));
        OnPropertyChanged(nameof(IsWindowActive));
        OnPropertyChanged(nameof(IsAtLatest));
        TryAdvanceReadState();
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        CancelSearch();
        LoadOlderCommand.Cancel();
        LoadNewerCommand.Cancel();
        JumpToLatestCommand.Cancel();
        JumpToFirstUnreadCommand.Cancel();
        LoadMoreSearchResultsCommand.Cancel();
        var pending = new[]
        {
            LoadOlderCommand.ExecutionTask,
            LoadNewerCommand.ExecutionTask,
            JumpToLatestCommand.ExecutionTask,
            JumpToFirstUnreadCommand.ExecutionTask,
            LoadMoreSearchResultsCommand.ExecutionTask,
        }.OfType<Task>();
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        Task[] readTasks;
        lock (_readTasksGate)
        {
            readTasks = [.. _readTasks];
        }
        await Task.WhenAll(readTasks).ConfigureAwait(false);

        Task[] searchTasks;
        lock (_searchTasksGate)
        {
            searchTasks = [.. _searchTasks];
        }
        try
        {
            await Task.WhenAll(searchTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task<bool> JumpToSearchResultAsync(
        HistorySearchResultListItem result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var position = result.Position;
        var version = Volatile.Read(ref _contextVersion);
        if (!IsCurrent(position.NodeId, position.ConversationId, version))
        {
            return false;
        }

        var opened = false;
        using var linked = CreateLinkedCancellation(cancellationToken);
        await _loadGate.WaitAsync(linked.Token);
        try
        {
            var page = await _history.GetMessagesAroundAsync(
                position,
                beforeLimit: 20,
                afterLimit: PageSize - 21,
                linked.Token);
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (!IsCurrent(position.NodeId, position.ConversationId, version))
                    {
                        return;
                    }

                    _highlightedSearchSequence = position.LocalSequence;
                    ApplyReplacement(page, requestScrollToEnd: false);
                    opened = true;
                    ScrollRequested?.Invoke(
                        this,
                        new HistoryScrollRequestEventArgs(HistoryScrollIntent.ToMessage, position.LocalSequence));
                },
                linked.Token);
        }
        catch (KeyNotFoundException)
        {
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (IsCurrent(position.NodeId, position.ConversationId, version))
                    {
                        _searchErrorMessage = "Сообщение больше не существует.";
                        RaiseSearchProperties();
                    }
                },
                linked.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Could not open a local history search result.");
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (IsCurrent(position.NodeId, position.ConversationId, version))
                    {
                        _searchErrorMessage = "Не удалось открыть найденное сообщение.";
                        RaiseSearchProperties();
                    }
                },
                CancellationToken.None);
        }
        finally
        {
            _loadGate.Release();
        }

        return opened;
    }

    public void DismissSearchResults()
    {
        CancelSearch();
        Interlocked.Increment(ref _searchRevision);
        _searchText = string.Empty;
        _searchErrorMessage = null;
        _searchCursor = null;
        SearchResults.Clear();
        IsSearching = false;
        OnPropertyChanged(nameof(SearchText));
        RaiseSearchProperties();
    }

    private async Task LoadRelativeAsync(
        bool before,
        bool autoScrollToEnd,
        CancellationToken cancellationToken)
    {
        var nodeId = _nodeId;
        var conversationId = _conversationId;
        var boundary = before ? _firstPosition : _lastPosition;
        if (nodeId is null || conversationId is null || boundary is null ||
            (before ? !_hasEarlier : !_hasLater && !autoScrollToEnd))
        {
            return;
        }

        var version = Volatile.Read(ref _contextVersion);
        using var linked = CreateLinkedCancellation(cancellationToken);
        await _loadGate.WaitAsync(linked.Token);
        try
        {
            var page = before
                ? await _history.GetMessagesBeforeAsync(
                    nodeId.Value, conversationId.Value, boundary, PageSize, linked.Token)
                : await _history.GetMessagesAfterAsync(
                    nodeId.Value, conversationId.Value, boundary, PageSize, linked.Token);
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (!IsCurrent(nodeId.Value, conversationId, version))
                    {
                        return;
                    }

                    if (before)
                    {
                        ApplyPrepend(page);
                    }
                    else
                    {
                        ApplyAppend(page, autoScrollToEnd);
                    }
                },
                linked.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Could not load a bounded history page.");
            throw;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private void ApplyReplacement(HistoryMessagePage page, bool requestScrollToEnd)
    {
        if (page.Items.Count > 0) ViewportChanging?.Invoke(this, EventArgs.Empty);
        Messages.Clear();
        foreach (var message in page.Items)
        {
            Messages.Add(CreateListItem(message));
        }

        _firstPosition = page.FirstPosition;
        _lastPosition = page.LastPosition;
        _hasEarlier = page.HasEarlier;
        _hasLater = page.HasLater;
        _isAtLatest = !page.HasLater;
        PendingNewMessageCountCore = 0;
        RaiseStateProperties();
        if (requestScrollToEnd)
        {
            ScrollRequested?.Invoke(this, new HistoryScrollRequestEventArgs(HistoryScrollIntent.ToEnd));
        }
    }

    private void ApplyPrepend(HistoryMessagePage page)
    {
        var anchorSequence = Messages.FirstOrDefault()?.LocalSequence;
        var existing = Messages.Select(item => item.Id).ToHashSet();
        var additions = page.Items.Where(item => existing.Add(item.Id)).ToArray();
        if (additions.Length > 0) ViewportChanging?.Invoke(this, EventArgs.Empty);
        for (var index = additions.Length - 1; index >= 0; index--)
        {
            Messages.Insert(0, CreateListItem(additions[index]));
        }

        _hasEarlier = page.HasEarlier;
        if (additions.Length > 0)
        {
            _firstPosition = Position(Messages[0]);
        }

        if (TrimFromEnd())
        {
            _hasLater = true;
            _lastPosition = Position(Messages[^1]);
            _isAtLatest = false;
        }

        RaiseStateProperties();
        if (anchorSequence is not null && additions.Length > 0)
        {
            ScrollRequested?.Invoke(
                this,
                new HistoryScrollRequestEventArgs(HistoryScrollIntent.PreserveViewport, anchorSequence));
        }
    }

    private void ApplyAppend(HistoryMessagePage page, bool autoScrollToEnd)
    {
        var anchorSequence = Messages.LastOrDefault()?.LocalSequence;
        var existing = Messages.Select(item => item.Id).ToHashSet();
        var additions = page.Items.Where(item => existing.Add(item.Id)).ToArray();
        if (additions.Length > 0 || autoScrollToEnd) ViewportChanging?.Invoke(this, EventArgs.Empty);
        foreach (var message in additions)
        {
            Messages.Add(CreateListItem(message));
        }

        _hasLater = page.HasLater;
        if (Messages.Count > 0)
        {
            _lastPosition = Position(Messages[^1]);
        }

        if (TrimFromStart())
        {
            _hasEarlier = true;
            _firstPosition = Position(Messages[0]);
        }

        _isAtLatest = autoScrollToEnd && !page.HasLater;
        if (!page.HasLater && autoScrollToEnd)
        {
            PendingNewMessageCountCore = 0;
        }

        RaiseStateProperties();
        if (autoScrollToEnd)
        {
            ScrollRequested?.Invoke(this, new HistoryScrollRequestEventArgs(HistoryScrollIntent.ToEnd));
        }
        else if (anchorSequence is not null && additions.Length > 0)
        {
            ScrollRequested?.Invoke(
                this,
                new HistoryScrollRequestEventArgs(HistoryScrollIntent.PreserveViewport, anchorSequence));
        }
    }

    private bool TrimFromEnd()
    {
        var trimmed = false;
        while (Messages.Count > MaximumMessages)
        {
            Messages.RemoveAt(Messages.Count - 1);
            trimmed = true;
        }

        return trimmed;
    }

    private bool TrimFromStart()
    {
        var trimmed = false;
        while (Messages.Count > MaximumMessages)
        {
            Messages.RemoveAt(0);
            trimmed = true;
        }

        return trimmed;
    }

    private void ApplyEmpty()
    {
        Messages.Clear();
        _firstPosition = null;
        _lastPosition = null;
        _hasEarlier = false;
        _hasLater = false;
        _isAtLatest = false;
        _firstVisibleSequence = null;
        _lastVisibleSequence = null;
        _isWindowActive = false;
        _lastReadSequence = 0;
        _lastRequestedReadSequence = 0;
        _observedReadThroughSequence = 0;
        _unreadCount = 0;
        _firstUnreadPosition = null;
        _readErrorMessage = null;
        PendingNewMessageCountCore = 0;
        RaiseStateProperties();
    }

    private int PendingNewMessageCountCore
    {
        get => _pendingNewMessageCount;
        set
        {
            if (SetProperty(ref _pendingNewMessageCount, value, nameof(PendingNewMessageCount)))
            {
                OnPropertyChanged(nameof(HasPendingNewMessages));
                OnPropertyChanged(nameof(PendingNewMessagesLabel));
            }
        }
    }

    private void RaiseStateProperties()
    {
        OnPropertyChanged(nameof(HasConversation));
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(HasEmptyHistory));
        OnPropertyChanged(nameof(CanLoadOlder));
        OnPropertyChanged(nameof(CanLoadNewer));
        OnPropertyChanged(nameof(IsAtLatest));
        OnPropertyChanged(nameof(UnreadCount));
        OnPropertyChanged(nameof(HasUnreadMessages));
        OnPropertyChanged(nameof(UnreadLabel));
        OnPropertyChanged(nameof(FirstUnreadPosition));
        OnPropertyChanged(nameof(ReadErrorMessage));
        OnPropertyChanged(nameof(HasReadError));
        LoadOlderCommand.NotifyCanExecuteChanged();
        LoadNewerCommand.NotifyCanExecuteChanged();
        JumpToLatestCommand.NotifyCanExecuteChanged();
        JumpToFirstUnreadCommand.NotifyCanExecuteChanged();
        LoadMoreSearchResultsCommand.NotifyCanExecuteChanged();
    }

    private void ApplyReadState(ConversationReadState? state, bool clearError = true)
    {
        if (state is not null && state.LastReadSequence < _lastReadSequence)
        {
            return;
        }

        _lastReadSequence = state?.LastReadSequence ?? 0;
        _lastRequestedReadSequence = Math.Max(_lastRequestedReadSequence, _lastReadSequence);
        _observedReadThroughSequence = Math.Max(_observedReadThroughSequence, _lastReadSequence);
        _unreadCount = state?.UnreadCount ?? 0;
        _firstUnreadPosition = state?.FirstUnreadPosition;
        if (clearError)
        {
            _readErrorMessage = null;
        }
        RaiseStateProperties();
    }

    private void TryAdvanceReadState()
    {
        if (Volatile.Read(ref _stopped) != 0 ||
            !_isWindowActive ||
            _nodeId is not { } nodeId ||
            _conversationId is not { } conversationId ||
            _firstUnreadPosition is not { } firstUnread ||
            _firstVisibleSequence is not { } firstVisible ||
            _lastVisibleSequence is not { } lastVisible)
        {
            return;
        }

        var firstVisibleIndex = IndexOfSequence(firstVisible);
        var lastVisibleIndex = IndexOfSequence(lastVisible);
        var observedIndex = IndexOfSequence(_observedReadThroughSequence);
        var startsAtUnreadBoundary =
            firstUnread.LocalSequence >= firstVisible &&
            firstUnread.LocalSequence <= lastVisible;
        var continuesObservedRange =
            _observedReadThroughSequence > _lastReadSequence &&
            observedIndex >= 0 &&
            firstVisibleIndex >= 0 &&
            firstVisibleIndex <= observedIndex + 1 &&
            lastVisibleIndex > observedIndex;
        if (!startsAtUnreadBoundary && !continuesObservedRange)
        {
            return;
        }

        var target = lastVisibleIndex >= 0 ? Messages[lastVisibleIndex] : null;
        if (target is null || target.LocalSequence <= _observedReadThroughSequence)
        {
            return;
        }

        _observedReadThroughSequence = target.LocalSequence;
        if (target.LocalSequence <= _lastRequestedReadSequence)
        {
            return;
        }

        _lastRequestedReadSequence = target.LocalSequence;
        var version = Volatile.Read(ref _contextVersion);
        TrackReadTask(() => AdvanceReadStateAsync(
            new HistoryMessagePosition(nodeId, conversationId, target.Id, target.LocalSequence),
            version));
    }

    private async Task AdvanceReadStateAsync(HistoryMessagePosition through, long version)
    {
        try
        {
            var state = await _readWrites.AdvanceAsync(through, CancellationToken.None).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (!IsCurrent(through.NodeId, through.ConversationId, version))
                    {
                        return;
                    }

                    ApplyReadState(state);
                    ReadStateChanged?.Invoke(state);
                    TryAdvanceReadState();
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not persist the visible conversation read position.");
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (IsCurrent(through.NodeId, through.ConversationId, version))
                    {
                        _lastRequestedReadSequence = _lastReadSequence;
                        _observedReadThroughSequence = _lastReadSequence;
                        _readErrorMessage = "Не удалось сохранить позицию чтения.";
                        RaiseStateProperties();
                    }
                },
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private int IndexOfSequence(long sequence)
    {
        for (var index = 0; index < Messages.Count; index++)
        {
            if (Messages[index].LocalSequence == sequence)
            {
                return index;
            }
        }

        return -1;
    }

    private void TrackReadTask(Func<Task> createTask)
    {
        Task task;
        lock (_readTasksGate)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                return;
            }

            task = createTask();
            _readTasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_readTasksGate)
                {
                    _readTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private HistoryMessagePosition Position(HistoryMessageListItem message) =>
        new(_nodeId!.Value, _conversationId!.Value, message.Id, message.LocalSequence);

    private HistoryMessageListItem CreateListItem(HistoryMessage message) =>
        new(message, message.LocalSequence == _highlightedSearchSequence);

    private void QueueSearch()
    {
        var revision = Interlocked.Increment(ref _searchRevision);
        CancelSearch();
        SearchResults.Clear();
        _searchCursor = null;
        _searchErrorMessage = null;
        ClearSearchHighlight();
        if (!IsSearchActive || _nodeId is null || _conversationId is null)
        {
            IsSearching = false;
            RaiseSearchProperties();
            return;
        }

        IsSearching = true;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _searchCancellation = cancellation;
        TrackSearch(SearchAfterDelayAsync(
            _nodeId.Value,
            _conversationId.Value,
            SearchText,
            revision,
            cancellation));
    }

    private async Task SearchAfterDelayAsync(
        Guid nodeId,
        Guid conversationId,
        string query,
        long revision,
        CancellationTokenSource cancellation)
    {
        try
        {
            await _searchDelay.DelayAsync(TimeSpan.FromMilliseconds(250), cancellation.Token);
            var page = await _history.SearchMessagesAsync(
                nodeId,
                conversationId,
                query,
                before: null,
                SearchPageSize,
                cancellation.Token);
            await _dispatcher.InvokeAsync(
                () => ApplySearchPage(page, replace: true, nodeId, conversationId, query, revision),
                cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not search the local conversation history.");
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (IsCurrentSearch(nodeId, conversationId, query, revision))
                    {
                        _searchErrorMessage = "Не удалось выполнить локальный поиск.";
                        IsSearching = false;
                        RaiseSearchProperties();
                    }
                },
                CancellationToken.None);
        }
        finally
        {
            lock (_searchTasksGate)
            {
                if (ReferenceEquals(_searchCancellation, cancellation))
                {
                    _searchCancellation = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private async Task LoadMoreSearchResultsAsync(CancellationToken cancellationToken)
    {
        var nodeId = _nodeId;
        var conversationId = _conversationId;
        var cursor = _searchCursor;
        var query = SearchText;
        var revision = Volatile.Read(ref _searchRevision);
        if (nodeId is null || conversationId is null || cursor is null || query.Length == 0)
        {
            return;
        }

        using var linked = CreateLinkedCancellation(cancellationToken);
        IsSearching = true;
        try
        {
            var page = await _history.SearchMessagesAsync(
                nodeId.Value,
                conversationId.Value,
                query,
                cursor,
                SearchPageSize,
                linked.Token);
            await _dispatcher.InvokeAsync(
                () => ApplySearchPage(
                    page,
                    replace: false,
                    nodeId.Value,
                    conversationId.Value,
                    query,
                    revision),
                linked.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Could not load more local history search results.");
            if (IsCurrentSearch(nodeId.Value, conversationId.Value, query, revision))
            {
                _searchErrorMessage = "Не удалось продолжить локальный поиск.";
                RaiseSearchProperties();
            }
        }
        finally
        {
            if (IsCurrentSearch(nodeId.Value, conversationId.Value, query, revision))
            {
                IsSearching = false;
            }
        }
    }

    private void ApplySearchPage(
        HistoryMessageSearchPage page,
        bool replace,
        Guid nodeId,
        Guid conversationId,
        string query,
        long revision)
    {
        if (!IsCurrentSearch(nodeId, conversationId, query, revision))
        {
            return;
        }

        if (replace)
        {
            SearchResults.Clear();
        }
        var existing = SearchResults.Select(item => item.Position.MessageId).ToHashSet();
        foreach (var item in page.Items)
        {
            if (existing.Add(item.Position.MessageId))
            {
                SearchResults.Add(new HistorySearchResultListItem(item));
            }
        }
        _searchCursor = page.NextCursor;
        _searchErrorMessage = null;
        IsSearching = false;
        RaiseSearchProperties();
    }

    private bool IsCurrentSearch(
        Guid nodeId,
        Guid conversationId,
        string query,
        long revision) =>
        IsCurrent(nodeId, conversationId, Volatile.Read(ref _contextVersion)) &&
        _conversationId == conversationId &&
        SearchText == query &&
        Volatile.Read(ref _searchRevision) == revision;

    private void ResetSearch()
    {
        CancelSearch();
        Interlocked.Increment(ref _searchRevision);
        _searchText = string.Empty;
        _searchErrorMessage = null;
        _searchCursor = null;
        _highlightedSearchSequence = null;
        SearchResults.Clear();
        IsSearching = false;
        OnPropertyChanged(nameof(SearchText));
        RaiseSearchProperties();
    }

    private void CancelSearch()
    {
        lock (_searchTasksGate)
        {
            _searchCancellation?.Cancel();
            _searchCancellation = null;
        }
    }

    private void TrackSearch(Task task)
    {
        lock (_searchTasksGate)
        {
            _searchTasks.Add(task);
        }
        _ = task.ContinueWith(
            completed =>
            {
                lock (_searchTasksGate)
                {
                    _searchTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ClearSearchHighlight()
    {
        _highlightedSearchSequence = null;
        foreach (var message in Messages)
        {
            message.IsSearchMatch = false;
        }
    }

    private void RaiseSearchProperties()
    {
        OnPropertyChanged(nameof(IsSearchActive));
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(CanLoadMoreSearchResults));
        OnPropertyChanged(nameof(SearchStatus));
        OnPropertyChanged(nameof(HasSearchStatus));
        LoadMoreSearchResultsCommand.NotifyCanExecuteChanged();
    }

    private Task ApplyAsync(Action action, bool dispatch, CancellationToken cancellationToken)
    {
        if (dispatch)
        {
            return _dispatcher.InvokeAsync(action, cancellationToken);
        }

        action();
        return Task.CompletedTask;
    }

    private CancellationTokenSource CreateLinkedCancellation(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);

    private bool IsCurrent(Guid nodeId, Guid? conversationId, long version) =>
        Volatile.Read(ref _stopped) == 0 &&
        _nodeId == nodeId &&
        _conversationId == conversationId &&
        Volatile.Read(ref _contextVersion) == version;

    private static HistoryMessagePage EmptyPage { get; } = new([], null, null, false, false);
}
