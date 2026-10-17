using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Read projection and local CAS preference for the selected stable channel, independent of slots.</summary>
public sealed class ChannelRegionViewModel : ObservableObject, IDisposable
{
    private readonly ChatWorkspaceViewModel _workspace;
    private readonly IRegionService _service;
    private readonly IRegionStore? _store;
    private readonly IConversationDirectoryReader _directory;
    private readonly ISessionCommandGateway? _commands;
    private readonly RegionSettingsViewModel _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<Task, Task> _track;
    private readonly CancellationTokenSource _stop = new();
    private long _revision;
    private bool _stopped, _saving, _initialized;
    private string _text = "Регион: неизвестен";
    private string? _error;
    public ChannelRegionViewModel(ChatWorkspaceViewModel workspace, IRegionService service, IRegionStore? store,
        IConversationDirectoryReader directory, ISessionCommandGateway? commands, RegionSettingsViewModel settings,
        IUiDispatcher dispatcher, Func<Task, Task> track)
    {
        _workspace = workspace; _service = service; _store = store; _directory = directory;
        _commands = commands; _settings = settings; _dispatcher = dispatcher; _track = track;
        workspace.Navigation.PropertyChanged += OnNavigation; workspace.PropertyChanged += OnWorkspace;
        settings.PropertyChanged += OnSettings; service.Changed += OnCommitted;
        workspace.Menu.AddRegionMenu(Choices);
    }
    public ObservableCollection<ConversationMenuItemViewModel> Choices { get; } = [];
    public string Text { get => _text; private set => SetProperty(ref _text, value); }
    public string? Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => Error is not null;
    public bool Visible => _workspace.SelectedConversation?.Kind == ConversationKind.Channel;
    public void Refresh() { if (!_stopped && _initialized) _ = _track(RefreshAsync()); }
    public async Task InitializeAsync() { await RefreshAsync(false); _initialized = true; }
    public async Task RefreshAsync(bool dispatch = true)
    {
        if (_stopped) return;
        var revision = ++_revision; var selected = _workspace.SelectedConversation;
        OnPropertyChanged(nameof(Visible));
        Choices.Clear(); Text = "Регион: неизвестен";
        if (selected is null || selected.Kind != ConversationKind.Channel || _workspace.ViewedNode?.Id != selected.NodeId) return;
        var node = selected.NodeId; var identity = selected.Entry.Identity.ToArray();
        try
        {
            var details = await _directory.GetChannelDetailsAsync(node, identity, _stop.Token).ConfigureAwait(false);
            if (details is null) return;
            var preference = await _service.GetChannelPreferenceAsync(node, details.Id, _stop.Token).ConfigureAwait(false);
            var regions = await _service.GetRegionsAsync(node, true, _stop.Token).ConfigureAwait(false);
            var snapshot = _commands?.GetDefaultRegion(node);
            snapshot ??= _store is not null ? await _store.GetNodeSnapshotAsync(node, _stop.Token).ConfigureAwait(false) : null;
            void Apply()
            {
                if (!Current(revision)) return;
                var text = preference.Mode switch
                {
                    FloodScopeMode.ForceUnscoped => "без региона",
                    FloodScopeMode.ExplicitRegion => regions.FirstOrDefault(r => r.Id == preference.RegionId)?.ProtocolName ?? "неизвестен",
                    _ => RegionSettingsViewModel.ScopeName(snapshot) + " (как у ноды" + (snapshot?.Freshness == RegionSnapshotFreshness.Offline ? "; offline" : "") + ")",
                };
                Text = "Регион: " + text;
                AddChoice("Как у ноды", FloodScopeMode.InheritDefault, null);
                AddChoice("Без региона", FloodScopeMode.ForceUnscoped, null);
                foreach (var region in regions.Where(r => !r.Archived)) AddChoice(region.Label + " — " + region.ProtocolName, FloodScopeMode.ExplicitRegion, region.Id);
                if (!regions.Any(r => !r.Archived)) Choices.Add(new(ConversationMenuAction.Region, "Добавьте регионы в Настройках", new RelayCommand(() => { }, () => false)));
                void AddChoice(string label, FloodScopeMode mode, Guid? regionId)
                {
                    var active = mode == preference.Mode && regionId == preference.RegionId;
                    var command = new AsyncRelayCommand(() => _track(SaveAsync(node, details.Id, preference.Revision, mode, regionId, revision)),
                        () => Current(revision) && !_saving);
                    Choices.Add(new(ConversationMenuAction.Region, (active ? "✓ " : "") + label, command));
                }
            }
            if (dispatch) await _dispatcher.InvokeAsync(Apply, _stop.Token); else Apply();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception)
        {
            void Fail() { if (Current(revision)) Error = "Не удалось прочитать регион канала."; }
            if (dispatch) await _dispatcher.InvokeAsync(Fail); else Fail();
        }
    }
    private async Task SaveAsync(Guid node, Guid channel, long expectedRevision, FloodScopeMode mode, Guid? region, long revision)
    {
        if (!Current(revision) || _saving) return;
        _saving = true; Error = null;
        try { await _service.AssignChannelAsync(node, channel, mode, region, expectedRevision, _stop.Token); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (Current(revision)) Error = error is RegionPreferenceConflictException
                    ? "Регион канала изменился. Меню обновлено; выберите регион заново." : "Не удалось сохранить регион канала. Повторите выбор.";
            });
        }
        finally { _saving = false; if (!_stopped) await _dispatcher.InvokeAsync(Refresh); }
    }
    private bool Current(long revision) => !_stopped && revision == _revision;
    private void OnNavigation(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(ConversationNavigationViewModel.SelectedConversation)) { Error = null; Refresh(); } }
    private void OnWorkspace(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(ChatWorkspaceViewModel.ViewedNode)) { Error = null; Refresh(); } }
    private void OnSettings(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(RegionSettingsViewModel.DefaultSnapshot) or nameof(RegionSettingsViewModel.IsOnline)) Refresh(); }
    private void OnCommitted(object? sender, RegionDataCommit e)
    { if (!_stopped && e.NodeId == _workspace.ViewedNode?.Id) _ = _track(_dispatcher.InvokeAsync(Refresh)); }
    public void Dispose()
    {
        if (_stopped) return; _stopped = true; _revision++; _stop.Cancel();
        _workspace.Navigation.PropertyChanged -= OnNavigation; _workspace.PropertyChanged -= OnWorkspace;
        _settings.PropertyChanged -= OnSettings; _service.Changed -= OnCommitted;
    }
}
