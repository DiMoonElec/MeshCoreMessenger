using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Union of saved definitions and candidates for one visible settings visit.</summary>
public sealed class RegionCatalogSearchViewModel : ObservableObject, IDisposable
{
    private readonly IRegionService? _service;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<Task, Task> _track;
    private readonly Func<SessionCommandOwner?> _owner;
    private readonly Dictionary<string, RegionCatalogRowViewModel> _rows = new(StringComparer.Ordinal);
    private CancellationTokenSource _visit = new();
    private CancellationTokenSource? _search;
    private SessionCommandOwner? _searchOwner;
    private Guid? _node;
    private long _epoch, _read;
    private bool _active, _disposed, _searching, _cancelling;

    public RegionCatalogSearchViewModel(IRegionService? service, IUiDispatcher dispatcher, Func<Task, Task> track,
        Func<SessionCommandOwner?> owner)
    {
        _service = service; _dispatcher = dispatcher; _track = track; _owner = owner;
        SearchCommand = new(() => _track(SearchAsync()), () => _active && !_disposed && !_searching && _service is not null && CurrentOwner() is not null);
        CancelCommand = new(Cancel, () => IsSearching && !_cancelling);
        if (service is not null) service.Changed += OnChanged;
    }
    public ObservableCollection<RegionCatalogRowViewModel> Rows { get; } = [];
    public AsyncRelayCommand SearchCommand { get; }
    public RelayCommand CancelCommand { get; }
    public event Action<string>? ErrorOccurred;
    public bool IsSearching
    {
        get => _searching;
        private set { if (SetProperty(ref _searching, value)) NotifyAvailability(); }
    }
    public void SetNode(Guid? node)
    {
        if (_disposed || node == _node) return;
        ResetVisit(); _node = node;
        if (_active) _ = _track(RefreshAsync());
        NotifyAvailability();
    }
    public void SetActive(bool active)
    {
        if (_disposed || active == _active) return;
        _active = active; ResetVisit();
        if (active) _ = _track(RefreshAsync());
        NotifyAvailability();
    }
    private void ResetVisit()
    {
        _epoch++; _read++;
        _visit.Cancel(); _visit.Dispose(); _visit = new();
        if (_search is not null) Cancel();
        _rows.Clear(); Rows.Clear();
    }
    public void NotifyAvailability()
    {
        var current = CurrentOwner();
        if (_searchOwner is { } prior && (current is null || !SameOwner(prior, current))) Cancel();
        SearchCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }
    private static bool SameOwner(SessionCommandOwner left, SessionCommandOwner right) =>
        left.NodeId == right.NodeId && left.SessionId == right.SessionId && left.Generation == right.Generation;
    private SessionCommandOwner? CurrentOwner() => _owner() is { } owner && owner.NodeId == _node ? owner : null;
    private bool Current(long epoch) => !_disposed && _active && epoch == _epoch;
    private void Cancel()
    {
        if (_search is null || _cancelling) return;
        _cancelling = true; CancelCommand.NotifyCanExecuteChanged();
        _ = _track(_search.CancelAsync());
    }
    public async Task RefreshAsync()
    {
        if (!_active || _disposed || _service is null || _node is not { } node) return;
        var epoch = _epoch; var read = ++_read; var token = _visit.Token;
        try
        {
            var saved = await _service.GetRegionsAsync(node, true, token).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                if (!Current(epoch) || read != _read) return;
                foreach (var row in _rows.Values) row.SetSaved(null);
                foreach (var entry in saved)
                    GetRow(entry.Definition, entry.Label).SetSaved(entry);
                SortRows();
            }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { await ReportAsync(epoch, "Не удалось прочитать каталог регионов."); }
    }
    private RegionCatalogRowViewModel GetRow(RegionDefinition definition, string? label = null)
    {
        var identity = Convert.ToHexString(definition.Key.ToArray());
        if (_rows.TryGetValue(identity, out var row)) return row;
        row = new(definition, label ?? definition.Name, candidate => _track(ToggleAsync(candidate)));
        _rows.Add(identity, row); Rows.Add(row); return row;
    }
    private void MergeNames(IReadOnlyList<DiscoveredRegionName> names)
    {
        foreach (var name in names)
        {
            if (name.Kind != DiscoveredRegionNameKind.Public || name.ExactName == "*" || name.ExactName.StartsWith('$')) continue;
            try { GetRow(RegionDefinition.Public(name.ExactName)); }
            catch (ArgumentException) { /* The UI catalogue only accepts valid public definitions. */ }
        }
        SortRows();
    }
    private void SortRows()
    {
        var sorted = _rows.OrderBy(p => p.Value.Name, StringComparer.Ordinal).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray();
        for (var i = 0; i < sorted.Length; i++)
        {
            var index = Rows.IndexOf(sorted[i]);
            if (index != i) Rows.Move(index, i);
        }
    }
    private async Task ToggleAsync(RegionCatalogRowViewModel row)
    {
        if (!_active || _disposed || row.IsSaving || _node is not { } node || _service is null || !Rows.Contains(row)) return;
        var epoch = _epoch; var token = _visit.Token; var savedId = row.SavedId;
        row.IsSaving = true;
        try
        {
            RegionCatalogEntry? saved = null;
            if (savedId is { } id) await _service.DeleteAsync(node, id, token);
            else if (row.Definition.Kind == RegionKeyKind.PublicDerived)
                saved = await _service.CreatePublicAsync(node, row.Label, row.Definition.Name, token);
            else saved = await _service.ImportCustomAsync(node, row.Label, row.Definition.Name, row.Definition.Key.ToArray(), token);
            await _dispatcher.InvokeAsync(() => { if (Current(epoch)) row.SetSaved(saved); });
            await RefreshAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (RegionInUseException) { await ReportAsync(epoch, "Регион назначен каналам. Сначала смените их регион."); }
        catch (Exception) { await ReportAsync(epoch, "Не удалось изменить каталог регионов. Повторите действие."); }
        finally { await _dispatcher.InvokeAsync(() => row.IsSaving = false); }
    }
    private async Task SearchAsync()
    {
        if (!_active || _disposed || IsSearching || _service is null || CurrentOwner() is not { } owner) return;
        var epoch = _epoch; Guid? run = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_visit.Token);
        _search = stop; _searchOwner = owner; _cancelling = false; IsSearching = true;
        try
        {
            var progress = new SearchProgress(value =>
            {
                if (value.Owner is not { } observed || observed.NodeId != owner.NodeId || observed.SessionId != owner.SessionId || observed.Generation != owner.Generation) return;
                _ = _track(_dispatcher.InvokeAsync(() =>
                {
                    if (!Current(epoch) || !ReferenceEquals(_search, stop) ||
                        CurrentOwner() is { } current && !SameOwner(owner, current)) return;
                    run ??= value.RunId;
                    if (run == value.RunId && value.Names is { } names) MergeNames(names);
                }));
            });
            var result = await _service!.DiscoverAsync(owner, RegionDiscoveryOptions.DirectNeighbors(), progress, stop.Token);
            await _dispatcher.InvokeAsync(() =>
            {
                if (!Current(epoch) || CurrentOwner() is { } current && !SameOwner(owner, current) ||
                    result.Owner.NodeId != owner.NodeId || result.Owner.SessionId != owner.SessionId ||
                    result.Owner.Generation != owner.Generation || run is not null && run != result.RunId) return;
                MergeNames(result.Names);
                if (result.Error is not null && result.Error is not OperationCanceledException)
                    ErrorOccurred?.Invoke("Поиск регионов завершён с ошибкой. Полученные результаты сохранены в списке.");
                else if (result.Outcomes.Any(o => o.HasRouteWarning))
                    ErrorOccurred?.Invoke("Не удалось подтвердить восстановление маршрута репитера.");
            });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception) { await ReportAsync(epoch, "Не удалось выполнить поиск регионов. Проверьте подключение."); }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                _search = null; _searchOwner = null; _cancelling = false; IsSearching = false;
            });
        }
    }
    private Task ReportAsync(long epoch, string message) => _dispatcher.InvokeAsync(() => { if (Current(epoch)) ErrorOccurred?.Invoke(message); });
    private void OnChanged(object? sender, RegionDataCommit commit)
    {
        if (_disposed) return;
        _ = _track(_dispatcher.InvokeAsync(() => { if (_active && commit.NodeId == _node) _ = _track(RefreshAsync()); }));
    }
    public void Dispose()
    {
        if (_disposed) return;
        _active = false; ResetVisit(); _disposed = true;
        if (_service is not null) _service.Changed -= OnChanged;
        NotifyAvailability(); _visit.Dispose();
    }
    private sealed class SearchProgress(Action<RegionDiscoveryProgress> apply) : IProgress<RegionDiscoveryProgress>
    { public void Report(RegionDiscoveryProgress value) => apply(value); }
}

public sealed class RegionCatalogRowViewModel : ObservableObject
{
    private bool _saving;
    private Guid? _savedId;
    internal RegionDefinition Definition { get; private set; }
    internal string Label { get; private set; }
    internal Guid? SavedId => _savedId;
    internal RegionCatalogRowViewModel(RegionDefinition definition, string label, Func<RegionCatalogRowViewModel, Task> toggle)
    {
        Definition = definition; Label = label;
        ToggleCommand = new(() => toggle(this), () => !IsSaving);
    }
    public string Name => string.IsNullOrEmpty(Definition.Name) ? Label : Definition.Name;
    public string Symbol => _savedId.HasValue ? "−" : "+";
    public string ActionName => (_savedId.HasValue ? "Удалить регион " : "Добавить регион ") + Name;
    public AsyncRelayCommand ToggleCommand { get; }
    public bool IsSaving { get => _saving; internal set { if (SetProperty(ref _saving, value)) ToggleCommand.NotifyCanExecuteChanged(); } }
    internal void SetSaved(RegionCatalogEntry? entry)
    {
        _savedId = entry?.Id;
        if (entry is not null) { Definition = entry.Definition; Label = entry.Label; }
        OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(Symbol)); OnPropertyChanged(nameof(ActionName));
    }
}
