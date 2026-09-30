using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;

namespace MeshCoreMessenger.Desktop.ViewModels;

public sealed class HistoryScrollRequestEventArgs(long? anchorSequence, bool scrollToEnd) : EventArgs
{
    public long? AnchorSequence { get; } = anchorSequence;
    public bool ScrollToEnd { get; } = scrollToEnd;
}

/// <summary>Owns one bounded, node-scoped message viewport.</summary>
public sealed class HistoryWindowViewModel : ObservableObject
{
    internal const int PageSize = 100;
    internal const int MaximumMessages = 500;

    private readonly ILocalHistoryReader _history;
    private readonly IConversationReadStateStore _readStates;
    private readonly IDurableReadStateWrites _readWrites;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly HashSet<Guid> _handledCommitIds = [];
    private readonly object _readTasksGate = new();
    private readonly HashSet<Task> _readTasks = [];
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
    private long _contextVersion;
    private int _stopped;

    public HistoryWindowViewModel(
        ILocalHistoryReader history,
        IConversationReadStateStore readStates,
        IDurableReadStateWrites readWrites,
        IUiDispatcher dispatcher,
        ILogger logger)
    {
        _history = history;
        _readStates = readStates;
        _readWrites = readWrites;
        _dispatcher = dispatcher;
        _logger = logger;
        LoadOlderCommand = new AsyncRelayCommand(LoadOlderAsync, () => CanLoadOlder);
        LoadNewerCommand = new AsyncRelayCommand(LoadNewerAsync, () => CanLoadNewer);
        JumpToLatestCommand = new AsyncRelayCommand(JumpToLatestAsync, () => HasConversation);
        JumpToFirstUnreadCommand = new AsyncRelayCommand(
            JumpToFirstUnreadAsync,
            () => FirstUnreadPosition is not null);
    }

    public event EventHandler<HistoryScrollRequestEventArgs>? ScrollRequested;
    public event Action<ConversationReadState>? ReadStateChanged;

    public ObservableCollection<HistoryMessageListItem> Messages { get; } = [];
    public IAsyncRelayCommand LoadOlderCommand { get; }
    public IAsyncRelayCommand LoadNewerCommand { get; }
    public IAsyncRelayCommand JumpToLatestCommand { get; }
    public IAsyncRelayCommand JumpToFirstUnreadCommand { get; }
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

    public void Clear()
    {
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
                            new HistoryScrollRequestEventArgs(position.LocalSequence, scrollToEnd: false));
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
        LoadOlderCommand.Cancel();
        LoadNewerCommand.Cancel();
        JumpToLatestCommand.Cancel();
        JumpToFirstUnreadCommand.Cancel();
        var pending = new[]
        {
            LoadOlderCommand.ExecutionTask,
            LoadNewerCommand.ExecutionTask,
            JumpToLatestCommand.ExecutionTask,
            JumpToFirstUnreadCommand.ExecutionTask,
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
        Messages.Clear();
        foreach (var message in page.Items)
        {
            Messages.Add(new HistoryMessageListItem(message));
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
            ScrollRequested?.Invoke(this, new HistoryScrollRequestEventArgs(null, scrollToEnd: true));
        }
    }

    private void ApplyPrepend(HistoryMessagePage page)
    {
        var anchorSequence = Messages.FirstOrDefault()?.LocalSequence;
        var existing = Messages.Select(item => item.Id).ToHashSet();
        var additions = page.Items.Where(item => existing.Add(item.Id)).ToArray();
        for (var index = additions.Length - 1; index >= 0; index--)
        {
            Messages.Insert(0, new HistoryMessageListItem(additions[index]));
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
                new HistoryScrollRequestEventArgs(anchorSequence, scrollToEnd: false));
        }
    }

    private void ApplyAppend(HistoryMessagePage page, bool autoScrollToEnd)
    {
        var anchorSequence = Messages.LastOrDefault()?.LocalSequence;
        var existing = Messages.Select(item => item.Id).ToHashSet();
        foreach (var message in page.Items)
        {
            if (existing.Add(message.Id))
            {
                Messages.Add(new HistoryMessageListItem(message));
            }
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
            ScrollRequested?.Invoke(this, new HistoryScrollRequestEventArgs(null, scrollToEnd: true));
        }
        else if (anchorSequence is not null && page.Items.Count > 0)
        {
            ScrollRequested?.Invoke(
                this,
                new HistoryScrollRequestEventArgs(anchorSequence, scrollToEnd: false));
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
