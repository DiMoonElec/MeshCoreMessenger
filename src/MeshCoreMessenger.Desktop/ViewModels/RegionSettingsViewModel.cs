using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Node defaults and the independently owned catalogue search card.</summary>
public sealed partial class RegionSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IRegionService? _service;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<Task, Task> _track;
    private Guid? _node;
    private long _context, _read;
    private bool _initialized, _stopped;
    private CancellationTokenSource _contextStop = new();
    private string? _error;
    private bool _busy;

    public RegionSettingsViewModel(IRegionService? service, IUiDispatcher dispatcher, Func<Task, Task> track)
    {
        _service = service; _dispatcher = dispatcher; _track = track;
        CatalogSearch = new(service, dispatcher, track, TryCaptureSearchOwner);
        if (service is not null) service.Changed += OnChanged;
    }
    public RegionCatalogSearchViewModel CatalogSearch { get; }
    public ObservableCollection<RegionCatalogEntry> AvailableRegions { get; } = [];
    public bool CanEdit => !_stopped && _service is not null && _node.HasValue && !IsBusy;
    public string? Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => Error is not null;
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) NotifyCommands(); } }
    public void SetNode(Guid? node, string? label)
    {
        if (_node == node || _stopped) return;
        _contextStop.Cancel(); _contextStop.Dispose(); _contextStop = new(); _context++; _read++;
        _node = node; Error = null; IsBusy = false;
        AvailableRegions.Clear(); SelectedDefaultRegion = null; _snapshot = null; OnPropertyChanged(nameof(DefaultSnapshot)); NotifyConnection();
        CatalogSearch.SetNode(node);
        NotifyCommands();
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
        catch (Exception) { await ShowErrorAsync(context, "Не удалось изменить регион ноды. Проверьте подключение."); }
        finally { await _dispatcher.InvokeAsync(() => { if (Current(context)) IsBusy = false; }); }
    }
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
    }
    public void Dispose()
    {
        if (_stopped) return; _stopped = true; _context++;
        if (_service is not null) _service.Changed -= OnChanged;
        CatalogSearch.Dispose();
        if (_supervisor is not null) _supervisor.StateChanged -= OnConnectionChanged;
        _contextStop.Cancel(); NotifyCommands();
    }
}
