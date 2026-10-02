using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Independent read-only device selection. Node identity is supplied by the root owner.</summary>
public sealed class DevicesWorkspaceViewModel : ObservableObject
{
    private const int PageSize = 50;
    private readonly IConversationDirectoryReader _reader;
    private readonly IUiDispatcher _dispatcher;
    private readonly ISearchDelay _delay;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _context;
    private CancellationToken _contextToken;
    private CancellationTokenSource? _search;
    private readonly object _gate = new();
    private readonly HashSet<Task> _pending = [];
    private Guid? _nodeId;
    private string _nodeLabel = "Нет истории подключённой ноды";
    private string _searchText = string.Empty;
    private DeviceListItem[] _rows = [];
    private ConversationDirectoryCursor? _cursor;
    private DeviceListItem? _selected;
    private DeviceDetailsViewModel? _details;
    private string? _listError;
    private string? _detailError;
    private bool _loading;
    private bool _detailLoading;
    private bool _narrow;
    private bool _showDetail;
    private long _nodeRevision;
    private long _listRevision;
    private long _detailRevision;
    private int _stopped;

    public DevicesWorkspaceViewModel(IConversationDirectoryReader reader, IUiDispatcher dispatcher,
        ISearchDelay delay, ILogger logger)
    {
        _reader = reader; _dispatcher = dispatcher; _delay = delay; _logger = logger;
        _context = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _contextToken = _context.Token;
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => CanRefresh);
        LoadMoreCommand = new AsyncRelayCommand(() => Run(() => LoadAsync(true, true, CancellationToken.None)), () => CanLoadMore);
        RetryDetailsCommand = new AsyncRelayCommand(() => Selected is { } item ? SelectAsync(item) : Task.CompletedTask,
            () => Selected is not null && !IsDetailLoading && Volatile.Read(ref _stopped) == 0);
        BackCommand = new RelayCommand(() => { _showDetail = false; Notify(); });
    }
    public ObservableCollection<DeviceListItem> Items { get; } = [];
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand LoadMoreCommand { get; }
    public IAsyncRelayCommand RetryDetailsCommand { get; }
    public IRelayCommand BackCommand { get; }
    public string NodeLabel => _nodeLabel;
    public DeviceListItem? Selected => _selected;
    public DeviceDetailsViewModel? Details => _details;
    public bool HasDetails => Details is not null;
    public bool IsLoading => _loading;
    public bool IsDetailLoading => _detailLoading;
    public bool CanRefresh => _nodeId is not null && !_loading && Volatile.Read(ref _stopped) == 0;
    public bool CanLoadMore => CanRefresh && _cursor is not null;
    public bool IsEmpty => !_loading && Items.Count == 0 && _listError is null;
    public string EmptyText => _nodeId is null ? "Нет истории подключённой ноды" :
        string.IsNullOrWhiteSpace(SearchText) ? "Известных устройств пока нет" : "Устройства не найдены";
    public string? ListError => _listError;
    public bool HasListError => _listError is not null;
    public string? DetailError => _detailError;
    public bool HasDetailError => _detailError is not null;
    public bool ShowPlaceholder => !HasDetails && !IsDetailLoading && !HasDetailError;
    public string PlaceholderText => Selected is null ? "Выберите устройство" : "Нет сохранённых сведений об устройстве";
    public bool IsListVisible => !_narrow || !_showDetail;
    public bool IsDetailVisible => !_narrow || _showDetail;
    public bool CanNavigateBack => _narrow && _showDetail;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Volatile.Read(ref _stopped) != 0 || !SetProperty(ref _searchText, value ?? string.Empty)) return;
            Interlocked.Increment(ref _listRevision); Interlocked.Increment(ref _detailRevision);
            _search?.Cancel();
            _selected = null; _details = null; _detailError = null; _detailLoading = false; _cursor = null;
            _rows = []; Items.Clear(); _showDetail = false; _loading = false; Notify();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_contextToken);
            _search = cancellation;
            Run(() => SearchAsync(cancellation));
        }
    }
    public void SetNarrowLayout(bool narrow) { _narrow = narrow; Notify(); }
    public void SetNode(Guid? nodeId, string? label)
    {
        if (Volatile.Read(ref _stopped) != 0) return;
        _nodeLabel = label ?? "Нет истории подключённой ноды";
        if (_nodeId == nodeId) { Notify(); return; }
        _context.Cancel(); _context.Dispose();
        _context = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _contextToken = _context.Token;
        Interlocked.Increment(ref _nodeRevision); Interlocked.Increment(ref _listRevision); Interlocked.Increment(ref _detailRevision);
        _nodeId = nodeId; _searchText = string.Empty; _rows = []; _cursor = null;
        _selected = null; _details = null; _listError = null; _detailError = null;
        _loading = false; _detailLoading = false; _showDetail = false;
        Items.Clear(); OnPropertyChanged(nameof(SearchText)); Notify();
    }
    public Task RefreshAsync(CancellationToken token = default, bool dispatchResult = true) =>
        Run(() => LoadAsync(false, dispatchResult, token));
    public Task SelectAsync(DeviceListItem item) => Run(() => SelectCoreAsync(item));
    private async Task SearchAsync(CancellationTokenSource cancellation)
    {
        try { await _delay.DelayAsync(TimeSpan.FromMilliseconds(300), cancellation.Token); await RefreshAsync(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_search, cancellation)) _search = null; cancellation.Dispose(); }
    }
    private async Task LoadAsync(bool append, bool dispatch, CancellationToken token)
    {
        if (_nodeId is not { } nodeId || Volatile.Read(ref _stopped) != 0 || (append && !CanLoadMore)) return;
        var nodeRevision = Volatile.Read(ref _nodeRevision);
        var revision = Interlocked.Increment(ref _listRevision);
        var query = SearchText.Trim(); var before = _rows; var cursor = append ? _cursor : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _contextToken);
        bool Current() => Volatile.Read(ref _stopped) == 0 && _nodeId == nodeId &&
            nodeRevision == Volatile.Read(ref _nodeRevision) && revision == Volatile.Read(ref _listRevision);
        try
        {
            await ApplyAsync(() => { if (Current()) { _loading = true; _listError = null; Notify(); } }, dispatch, linked.Token);
            var rows = new List<DeviceListItem>(append ? before : []);
            var target = append ? rows.Count + PageSize : Math.Max(PageSize, before.Length);
            do
            {
                var page = query.Length == 0
                    ? await _reader.GetPageAsync(nodeId, ConversationDirectorySection.ServiceContacts, cursor, PageSize, linked.Token)
                    : await _reader.SearchPageAsync(nodeId, ConversationDirectorySection.ServiceContacts, query, cursor, PageSize, linked.Token);
                rows.AddRange(page.Items.Where(entry => entry.NodeId == nodeId && entry.Section == ConversationDirectorySection.ServiceContacts && entry.ContactType != 1)
                    .Select(entry => new DeviceListItem(entry)));
                cursor = page.NextCursor;
            } while (!append && cursor is not null && rows.Count < target);
            await ApplyAsync(() =>
            {
                if (!Current()) return;
                _rows = rows.DistinctBy(item => item.Key).ToArray(); _cursor = cursor;
                // Preserve selection during collection replacement; null ListBox events are not user intent.
                _selected = _selected is { } selected ? _rows.FirstOrDefault(item => item.Key == selected.Key) ?? selected : null;
                Items.Clear(); foreach (var row in _rows) Items.Add(row);
                _loading = false; Notify();
            }, dispatch, linked.Token);
            if (!append && Current() && _selected is { } selection)
                await ReadDetailsAsync(selection, false, dispatch, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Could not read device directory.");
            await ApplyAsync(() => { if (Current()) { _listError = "Не удалось загрузить устройства. Повторите обновление."; Notify(); } }, dispatch, _lifetime.Token);
        }
        finally
        {
            if (Current()) await ApplyAsync(() => { if (Current()) { _loading = false; Notify(); } }, dispatch, _lifetime.Token);
        }
    }
    private async Task SelectCoreAsync(DeviceListItem item)
    {
        if (item.NodeId != _nodeId || !_rows.Any(row => row.Key == item.Key)) return;
        if (_selected?.Key == item.Key && (HasDetails || IsDetailLoading))
        { _showDetail = true; Notify(); return; }
        _selected = item; _showDetail = true; Notify();
        await ReadDetailsAsync(item, true, true, _contextToken);
    }
    private async Task ReadDetailsAsync(DeviceListItem item, bool clear, bool dispatch, CancellationToken token)
    {
        var nodeRevision = Volatile.Read(ref _nodeRevision);
        var revision = Interlocked.Increment(ref _detailRevision);
        bool Current() => Volatile.Read(ref _stopped) == 0 && _nodeId == item.NodeId && nodeRevision == Volatile.Read(ref _nodeRevision) &&
            revision == Volatile.Read(ref _detailRevision) && _selected?.Key == item.Key;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _contextToken);
        try
        {
            await ApplyAsync(() => { if (Current()) { _detailLoading = true; _detailError = null; if (clear) _details = null; Notify(); } }, dispatch, linked.Token);
            var details = await _reader.GetContactDetailsAsync(item.NodeId, item.PublicKey, linked.Token);
            await ApplyAsync(() =>
            {
                if (!Current()) return;
                if (details is not null && (details.NodeId != item.NodeId || !details.PublicKey.SequenceEqual(item.PublicKey)))
                    throw new InvalidDataException("Device projection has the wrong owner.");
                if (details?.ContactType == 1) { _selected = null; _details = null; Notify(); return; }
                _details = details is null ? null : new DeviceDetailsViewModel(details);
                if (details is not null && !_rows.Any(row => row.Key == item.Key))
                {
                    // Keep the selected row pinned if an activity reorder moved it beyond loaded pages.
                    var pinned = new DeviceListItem(details); _rows = [.. _rows, pinned]; Items.Add(pinned); _selected = pinned;
                }
                Notify();
            }, dispatch, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Could not read device details.");
            await ApplyAsync(() => { if (Current()) { _details = null; _detailError = "Не удалось прочитать карточку устройства."; Notify(); } }, dispatch, _lifetime.Token);
        }
        finally
        {
            if (Current()) await ApplyAsync(() => { if (Current()) { _detailLoading = false; Notify(); } }, dispatch, _lifetime.Token);
        }
    }
    private Task ApplyAsync(Action action, bool dispatch, CancellationToken token)
    { if (dispatch) return _dispatcher.InvokeAsync(action, token); token.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
    private Task Run(Func<Task> work)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _stopped) != 0) return Task.CompletedTask;
            var task = work(); _pending.Add(task);
            _ = task.ContinueWith(done => { lock (_gate) _pending.Remove(done); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }
    private void Notify()
    {
        foreach (var name in new[] { nameof(NodeLabel), nameof(Selected), nameof(Details), nameof(HasDetails), nameof(IsLoading),
            nameof(IsDetailLoading), nameof(CanRefresh), nameof(CanLoadMore), nameof(IsEmpty), nameof(EmptyText), nameof(ListError),
            nameof(HasListError), nameof(DetailError), nameof(HasDetailError), nameof(ShowPlaceholder), nameof(PlaceholderText),
            nameof(IsListVisible), nameof(IsDetailVisible), nameof(CanNavigateBack) }) OnPropertyChanged(name);
        RefreshCommand.NotifyCanExecuteChanged(); LoadMoreCommand.NotifyCanExecuteChanged(); RetryDetailsCommand.NotifyCanExecuteChanged();
    }
    public async Task StopAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            _lifetime.Cancel(); _context.Cancel(); pending = _pending.ToArray();
        }
        try { await Task.WhenAll(pending).ConfigureAwait(false); } catch (OperationCanceledException) { }
        _loading = false; _detailLoading = false;
        _context.Dispose(); _lifetime.Dispose();
    }
}
