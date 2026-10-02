using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Single-flight explicit user workflow around the existing supervisor; never owns a session.</summary>
public sealed class ConnectionControlViewModel : ObservableObject
{
    private readonly ConnectionProfilesViewModel _editor;
    private readonly IConnectionProfileManager _profiles;
    private readonly IConnectionSupervisor _supervisor;
    private readonly IUiDispatcher? _dispatcher;
    private readonly INodeStore? _nodes;
    private readonly TimeProvider _time;
    private readonly TimeSpan _disconnectTimeout;
    private readonly CancellationTokenSource _lifetime = new();
    private ConnectionSupervisorSnapshot _snapshot;
    private string? _error;
    private string _nodeName = "Не определена";
    private string _publicKey = string.Empty;
    private int _busy;
    private int _stopped;
    private long _stateVersion;
    private long _latestGeneration;
    private string _operationStatus = "Подключение…";
    private readonly object _pendingGate = new();
    private readonly HashSet<Task> _pending = [];

    public ConnectionControlViewModel(ConnectionProfilesViewModel editor, IConnectionProfileManager profiles,
        IConnectionSupervisor supervisor, IUiDispatcher? dispatcher = null, INodeStore? nodes = null,
        TimeProvider? time = null, TimeSpan? disconnectTimeout = null)
    {
        _editor = editor; _profiles = profiles; _supervisor = supervisor; _dispatcher = dispatcher; _nodes = nodes;
        _time = time ?? TimeProvider.System; _disconnectTimeout = disconnectTimeout ?? TimeSpan.FromSeconds(30);
        _snapshot = supervisor.Snapshot;
        _latestGeneration = _snapshot.Generation;
        // Non-cancelable command overload: a repeated ExecuteAsync must not cancel an earlier
        // workflow before its single-flight guard. Lifetime cancellation still stops shutdown work.
        ConnectCommand = new AsyncRelayCommand(() => Track(ConnectAsync(CancellationToken.None)), () => CanConnect);
        DisconnectCommand = new AsyncRelayCommand(() => Track(DisconnectAsync(CancellationToken.None)), () => CanDisconnect);
        _supervisor.StateChanged += OnStateChanged;
    }
    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand DisconnectCommand { get; }
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool CanConnect => Volatile.Read(ref _stopped) == 0 && !IsBusy && !_editor.IsBusy && !_editor.IsDirty &&
        !_editor.HasPendingSelection && _editor.IsValid && _editor.SavedProfile is not null &&
        _snapshot.State is not (ConnectionSupervisorState.Disconnecting or ConnectionSupervisorState.Connecting or
            ConnectionSupervisorState.Identifying or ConnectionSupervisorState.Synchronizing) &&
        (_snapshot.State != ConnectionSupervisorState.Online || NeedsReconnect);
    public bool CanDisconnect => Volatile.Read(ref _stopped) == 0 && !IsBusy &&
        _snapshot.State is not (ConnectionSupervisorState.Offline or ConnectionSupervisorState.Disconnecting);
    public bool NeedsReconnect => _snapshot.UsedProfile is { } used && _editor.SavedProfile is { } saved &&
        !ConnectionProfileEditorViewModel.SameSettings(used, saved);
    public bool HasUnappliedChanges => _snapshot.UsedProfile is { } used && _editor.SavedProfile is { } saved &&
        used.Id == saved.Id && NeedsReconnect && _snapshot.State != ConnectionSupervisorState.Offline;
    public string? UnappliedNotice => HasUnappliedChanges ? "Изменения сохранены, но ещё не применены к подключению." : null;
    public string? ConnectHint => _editor.IsDirty ? "Сохраните или отмените изменения перед подключением." :
        _editor.SavedProfile is null ? "Выберите сохранённый профиль для подключения." : null;
    public string ConnectLabel => NeedsReconnect && _snapshot.State != ConnectionSupervisorState.Offline ? "Переподключить" :
        _snapshot.State == ConnectionSupervisorState.RetryWaiting ? "Подключить сейчас" : "Подключить";
    public string Status => IsBusy ? _operationStatus : _snapshot.State switch
    {
        ConnectionSupervisorState.Offline => "○ Не подключено",
        ConnectionSupervisorState.Connecting => "◌ Подключение…",
        ConnectionSupervisorState.Identifying => "◌ Определение ноды…",
        ConnectionSupervisorState.Synchronizing => "◌ Синхронизация…",
        ConnectionSupervisorState.Online => "● Подключено",
        ConnectionSupervisorState.RetryWaiting => "◌ Ожидание повтора",
        ConnectionSupervisorState.Disconnecting => "◌ Отключение…",
        _ => "! Требуется внимание",
    };
    public bool IsOnline => !IsBusy && _snapshot.State == ConnectionSupervisorState.Online;
    public bool IsAttention => _snapshot.State == ConnectionSupervisorState.NeedsAttention || _error is not null;
    public string? ErrorMessage => _error ?? (_snapshot.State == ConnectionSupervisorState.NeedsAttention ? _snapshot.Reason : null);
    public string? Detail => _snapshot.State == ConnectionSupervisorState.NeedsAttention ? null : _snapshot.Reason;
    public string? RetryText => _snapshot.NextAttemptUtc is { } time ? $"Следующая попытка: {time.ToLocalTime():HH:mm:ss}" : null;
    public string ActiveProfileName => _snapshot.State == ConnectionSupervisorState.Offline ? "Нет активного подключения" :
        _snapshot.UsedProfile is { } used ? $"{used.Name} — {used.Transport}" : "Не определён";
    public string NodeName => _nodeName;
    public string PublicKey => _publicKey;
    public void RefreshActions()
    {
        foreach (var name in new[] { nameof(IsBusy), nameof(CanConnect), nameof(CanDisconnect), nameof(NeedsReconnect),
            nameof(HasUnappliedChanges), nameof(UnappliedNotice), nameof(ConnectHint), nameof(ConnectLabel), nameof(Status),
            nameof(IsOnline), nameof(IsAttention), nameof(ErrorMessage), nameof(Detail), nameof(RetryText),
            nameof(ActiveProfileName), nameof(NodeName), nameof(PublicKey) }) OnPropertyChanged(name);
        ConnectCommand.NotifyCanExecuteChanged(); DisconnectCommand.NotifyCanExecuteChanged();
    }
    public async Task LoadAsync(CancellationToken token = default)
    {
        await ReadNodeAsync(_snapshot, token);
        RefreshActions();
    }
    private Task Track(Task task)
    {
        lock (_pendingGate) _pending.Add(task);
        _ = task.ContinueWith(completed => { lock (_pendingGate) _pending.Remove(completed); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }
    private async Task ConnectAsync(CancellationToken token)
    {
        if (!CanConnect || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        var saved = _editor.SavedProfile!;
        var reconnect = NeedsReconnect;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        _operationStatus = _snapshot.State == ConnectionSupervisorState.Offline ? "Подключение…" : "Переподключение…";
        _error = null; RefreshActions(); _editor.NotifyControlChanged();
        try
        {
            if (_snapshot.State == ConnectionSupervisorState.RetryWaiting && !reconnect)
                await _supervisor.ConnectNowAsync(linked.Token);
            else
            {
                if (_snapshot.State != ConnectionSupervisorState.Offline)
                    await DisconnectWithTimeoutAsync(linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                if (reconnect)
                {
                    // With no active attempt, existing SwitchProfile selects and starts exactly
                    // once, and also replaces the cached profile during suspend. ConnectNow alone
                    // deliberately retains that cache for normal retries/wake.
                    await _supervisor.SwitchProfileAsync(saved.Id, linked.Token);
                }
                else
                {
                    await _profiles.SelectAsync(saved.Id, linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    await _supervisor.ConnectNowAsync(linked.Token);
                }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception error) { _error = error is TimeoutException ? $"Отключение не завершилось за {_disconnectTimeout.TotalSeconds:g} секунд. Новое подключение не начато." : $"Не удалось подключиться: {error.Message}"; }
        finally { Interlocked.Exchange(ref _busy, 0); RefreshActions(); _editor.NotifyControlChanged(); }
    }
    private async Task DisconnectAsync(CancellationToken token)
    {
        if (!CanDisconnect || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        _operationStatus = "Отключение…"; _error = null; RefreshActions(); _editor.NotifyControlChanged();
        try { await DisconnectWithTimeoutAsync(linked.Token); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception error) { _error = $"Не удалось завершить отключение: {error.Message}"; }
        finally { Interlocked.Exchange(ref _busy, 0); RefreshActions(); _editor.NotifyControlChanged(); }
    }
    private async Task DisconnectWithTimeoutAsync(CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(_disconnectTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try { await _supervisor.DisconnectAsync(linked.Token).WaitAsync(linked.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        { throw new TimeoutException("Истекло время ожидания отключения."); }
    }
    private void OnStateChanged(object? sender, ConnectionSupervisorStateChangedEventArgs args)
    {
        if (Volatile.Read(ref _stopped) != 0) return;
        var latest = Volatile.Read(ref _latestGeneration);
        if (args.Current.Generation < latest) return;
        Interlocked.Exchange(ref _latestGeneration, args.Current.Generation);
        var version = Interlocked.Increment(ref _stateVersion);
        Track(ApplySnapshotAsync(args.Current, version));
    }
    private async Task ApplySnapshotAsync(ConnectionSupervisorSnapshot snapshot, long version)
    {
        try
        {
            NodeRecord? node = null;
            string? nodeError = null;
            try
            {
                if (_nodes is not null && snapshot.NodeId is { } id) node = await _nodes.GetAsync(id, _lifetime.Token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { nodeError = "Не удалось прочитать локальную информацию о ноде."; }
            void Apply()
            {
                if (Volatile.Read(ref _stopped) != 0 || version != Volatile.Read(ref _stateVersion)) return;
                _snapshot = snapshot;
                if (nodeError is not null) _error = nodeError;
                _nodeName = node?.LastName ?? "Не определена";
                _publicKey = node is null ? string.Empty : Convert.ToHexString(node.PublicKey).ToLowerInvariant();
                RefreshActions();
            }
            if (_dispatcher is null) Apply(); else await _dispatcher.InvokeAsync(Apply, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { /* Node label failure must not control the session or terminate its loop. */ }
    }
    private async Task ReadNodeAsync(ConnectionSupervisorSnapshot snapshot, CancellationToken token)
    {
        if (_nodes is null || snapshot.NodeId is not { } id) return;
        var node = await _nodes.GetAsync(id, token);
        _nodeName = node?.LastName ?? "Не определена";
        _publicKey = node is null ? string.Empty : Convert.ToHexString(node.PublicKey).ToLowerInvariant();
    }
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _supervisor.StateChanged -= OnStateChanged; _lifetime.Cancel(); ConnectCommand.Cancel(); DisconnectCommand.Cancel();
        Task[] tasks; lock (_pendingGate) tasks = _pending.ToArray();
        await Task.WhenAll(tasks);
        RefreshActions(); _lifetime.Dispose();
    }
}
