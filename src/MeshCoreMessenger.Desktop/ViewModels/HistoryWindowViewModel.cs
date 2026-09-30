using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
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
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly HashSet<Guid> _handledCommitIds = [];
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
    private long _contextVersion;
    private int _stopped;

    public HistoryWindowViewModel(
        ILocalHistoryReader history,
        IUiDispatcher dispatcher,
        ILogger logger)
    {
        _history = history;
        _dispatcher = dispatcher;
        _logger = logger;
        LoadOlderCommand = new AsyncRelayCommand(LoadOlderAsync, () => CanLoadOlder);
        LoadNewerCommand = new AsyncRelayCommand(LoadNewerAsync, () => CanLoadNewer);
        JumpToLatestCommand = new AsyncRelayCommand(JumpToLatestAsync, () => HasConversation);
    }

    public event EventHandler<HistoryScrollRequestEventArgs>? ScrollRequested;

    public ObservableCollection<HistoryMessageListItem> Messages { get; } = [];
    public IAsyncRelayCommand LoadOlderCommand { get; }
    public IAsyncRelayCommand LoadNewerCommand { get; }
    public IAsyncRelayCommand JumpToLatestCommand { get; }
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
        using var linked = CreateLinkedCancellation(cancellationToken);
        var page = conversationId is { } id
            ? await _history.GetMessagesBeforeAsync(nodeId, id, null, PageSize, linked.Token)
            : EmptyPage;
        await ApplyAsync(
            () =>
            {
                if (!IsCurrent(nodeId, conversationId, version))
                {
                    return;
                }

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

    public async Task HandleCommittedMessageAsync(
        StoredIncomingMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!message.Inserted)
        {
            return;
        }

        var shouldLoad = false;
        await _dispatcher.InvokeAsync(
            () =>
            {
                if (message.NodeId != _nodeId ||
                    message.ConversationId != _conversationId ||
                    !_handledCommitIds.Add(message.MessageId) ||
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

    /// <summary>Reports what is actually visible; it does not persist read state.</summary>
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
        var pending = new[]
        {
            LoadOlderCommand.ExecutionTask,
            LoadNewerCommand.ExecutionTask,
            JumpToLatestCommand.ExecutionTask,
        }.OfType<Task>();
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
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
        LoadOlderCommand.NotifyCanExecuteChanged();
        LoadNewerCommand.NotifyCanExecuteChanged();
        JumpToLatestCommand.NotifyCanExecuteChanged();
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
