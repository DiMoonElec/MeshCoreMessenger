using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Node-scoped local catalogue. Accepted writes are awaited by the root UI lifetime.</summary>
public sealed partial class RegionSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IRegionService? _service;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<Task, Task> _track;
    private Guid? _node;
    private long _context, _read;
    private bool _initialized, _stopped;
    private CancellationTokenSource _contextStop = new();
    private RegionCatalogEntry? _selected;
    private string _label = "", _protocolName = "", _key = "", _nodeLabel = "Нода не выбрана";
    private string? _error;
    private bool _busy;

    public RegionSettingsViewModel(IRegionService? service, IUiDispatcher dispatcher, Func<Task, Task> track)
    {
        _service = service; _dispatcher = dispatcher; _track = track;
        AddPublicCommand = Command(AddPublicAsync); ImportCommand = Command(ImportAsync);
        RenameCommand = Command(RenameAsync, () => Selected is not null);
        ArchiveCommand = Command(ArchiveAsync, () => Selected is not null);
        DeleteCommand = Command(DeleteAsync, () => Selected is not null);
        RefreshCommand = new AsyncRelayCommand(() => _track(RefreshAsync()));
        if (service is not null) service.Changed += OnChanged;
    }
    private AsyncRelayCommand Command(Func<Guid, long, CancellationToken, Task> action, Func<bool>? enabled = null) =>
        new(() => _track(ExecuteAsync(action)), () => CanEdit && (enabled?.Invoke() ?? true));
    public ObservableCollection<RegionCatalogEntry> Catalog { get; } = [];
    public ObservableCollection<RegionCatalogEntry> AvailableRegions { get; } = [];
    public AsyncRelayCommand AddPublicCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand RenameCommand { get; }
    public AsyncRelayCommand ArchiveCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public bool CanEdit => !_stopped && _service is not null && _node.HasValue && !IsBusy;
    public string NodeLabel { get => _nodeLabel; private set => SetProperty(ref _nodeLabel, value); }
    public string Label { get => _label; set => SetProperty(ref _label, value); }
    public string ProtocolName { get => _protocolName; set => SetProperty(ref _protocolName, value); }
    public string KeyHex { get => _key; set => SetProperty(ref _key, value); }
    public string? Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => Error is not null;
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) NotifyCommands(); } }
    public RegionCatalogEntry? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) { if (value is not null) Label = value.Label; NotifyCommands(); } }
    }
    public void SetNode(Guid? node, string? label)
    {
        NodeLabel = label ?? "Нода не выбрана";
        if (_node == node || _stopped) return;
        _contextStop.Cancel(); _contextStop.Dispose(); _contextStop = new(); _context++; _read++;
        _node = node; Catalog.Clear(); Selected = null; Error = null; IsBusy = false;
        AvailableRegions.Clear(); SelectedDefaultRegion = null; _snapshot = null; OnPropertyChanged(nameof(DefaultSnapshot)); NotifyConnection();
        ResetDiscovery();
        Label = ""; ProtocolName = ""; KeyHex = ""; NotifyCommands();
        if (_initialized) _ = _track(RefreshAsync());
    }
    public async Task InitializeAsync() { await RefreshAsync(false); _initialized = true; }
    public async Task RefreshAsync(bool dispatch = true)
    {
        if (_stopped || _node is not { } node || _service is null) return;
        var context = _context; var read = ++_read; var token = _contextStop.Token;
        try
        {
            var items = await _service.GetRegionsAsync(node, true, token).ConfigureAwait(false);
            var snapshot = IsOnline ? _commands?.GetDefaultRegion(node) : null;
            snapshot ??= _store is not null ? await _store.GetNodeSnapshotAsync(node, token).ConfigureAwait(false) : null;
            void Apply()
            {
                if (!Current(context) || read != _read) return;
                var selected = Selected?.Id; Catalog.Clear(); foreach (var item in items) Catalog.Add(item);
                Selected = Catalog.FirstOrDefault(r => r.Id == selected);
                var defaultId = SelectedDefaultRegion?.Id;
                AvailableRegions.Clear(); foreach (var item in items.Where(r => !r.Archived)) AvailableRegions.Add(item);
                SelectedDefaultRegion = AvailableRegions.FirstOrDefault(r => r.Id == defaultId);
                _snapshot = snapshot; OnPropertyChanged(nameof(DefaultSnapshot)); NotifyConnection();
            }
            if (dispatch) await _dispatcher.InvokeAsync(Apply, token); else Apply();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { await ShowErrorAsync(context, "Не удалось прочитать каталог регионов.", dispatch); }
    }
    private async Task ExecuteAsync(Func<Guid, long, CancellationToken, Task> action)
    {
        if (!CanEdit || _node is not { } node) return;
        var context = _context; var token = _contextStop.Token; IsBusy = true; Error = null;
        try { await action(node, context, token); await RefreshAsync(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (RegionInUseException) { await ShowErrorAsync(context, "Регион назначен каналам. Сначала смените их регион или поместите запись в архив."); }
        catch (ArgumentException) { await ShowErrorAsync(context, "Проверьте название, протокольное имя и ключ (32 hex-символа для приватного региона)."); }
        catch (Exception) { await ShowErrorAsync(context, "Не удалось сохранить регион. Данные формы сохранены; повторите действие после устранения ошибки."); }
        finally { await _dispatcher.InvokeAsync(() => { if (Current(context)) IsBusy = false; }); }
    }
    private Task AddPublicAsync(Guid node, long context, CancellationToken token) =>
        _service!.CreatePublicAsync(node, Label, ProtocolName, token);
    private async Task ImportAsync(Guid node, long context, CancellationToken token)
    {
        byte[] key;
        try { key = Convert.FromHexString(KeyHex); } catch (FormatException) { throw new ArgumentException("Invalid key."); }
        await _service!.ImportCustomAsync(node, Label, ProtocolName, key, token);
        await _dispatcher.InvokeAsync(() => { if (Current(context)) KeyHex = ""; });
    }
    private Task RenameAsync(Guid node, long context, CancellationToken token) => _service!.RenameLabelAsync(node, Selected!.Id, Label, token);
    private Task ArchiveAsync(Guid node, long context, CancellationToken token) => _service!.SetArchivedAsync(node, Selected!.Id, !Selected.Archived, token);
    private Task DeleteAsync(Guid node, long context, CancellationToken token) => _service!.DeleteAsync(node, Selected!.Id, token);
    private bool Current(long context) => !_stopped && context == _context;
    private Task ShowErrorAsync(long context, string message, bool dispatch = true)
    {
        void Apply() { if (Current(context)) Error = message; }
        if (dispatch) return _dispatcher.InvokeAsync(Apply); Apply(); return Task.CompletedTask;
    }
    private void OnChanged(object? sender, RegionDataCommit commit)
    { if (!_stopped && _initialized && commit.NodeId == _node) _ = _track(_dispatcher.InvokeAsync(() => { if (!_stopped) _ = _track(RefreshAsync()); })); }
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanEdit));
        NotifyConnection();
        foreach (var command in new[] { AddPublicCommand, ImportCommand, RenameCommand, ArchiveCommand, DeleteCommand }) command?.NotifyCanExecuteChanged();
    }
    public void Dispose()
    {
        if (_stopped) return; _stopped = true; _context++;
        if (_service is not null) _service.Changed -= OnChanged;
        if (_supervisor is not null) _supervisor.StateChanged -= OnConnectionChanged;
        _contextStop.Cancel(); NotifyCommands();
    }
}
