using CommunityToolkit.Mvvm.Input;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Desktop.ViewModels;

public sealed partial class RegionSettingsViewModel
{
    private IConnectionSupervisor? _supervisor;
    private ISessionCommandGateway? _commands;
    private IRegionStore? _store;
    private RegionCatalogEntry? _defaultSelection;
    private NodeRegionSnapshot? _snapshot;
    public AsyncRelayCommand SetDefaultCommand { get; private set; } = null!;
    public AsyncRelayCommand ClearDefaultCommand { get; private set; } = null!;
    public RegionCatalogEntry? SelectedDefaultRegion
    {
        get => _defaultSelection;
        set { if (SetProperty(ref _defaultSelection, value)) SetDefaultCommand?.NotifyCanExecuteChanged(); }
    }
    public bool IsOnline => !_stopped && _node.HasValue && _supervisor?.Snapshot is { State: ConnectionSupervisorState.Online, SessionId: not null } active && active.NodeId == _node;
    public bool CanChangeDefault => CanEdit && IsOnline && _commands is not null && _commands.GetRegionCapabilities(_node!.Value)?.DefaultReadWrite != RegionSupport.Unsupported;
    public NodeRegionSnapshot? DefaultSnapshot => _snapshot;
    public string DefaultStatus => _snapshot?.Support == RegionSupport.Unsupported ? "Не поддерживается этой нодой" :
        (IsOnline ? "Текущий регион: " : "Последнее наблюдение (offline): ") + ScopeName(_snapshot);
    public static string ScopeName(NodeRegionSnapshot? snapshot) => snapshot?.Value.State switch
    {
        NodeFloodScopeState.KnownEmpty => "без региона",
        NodeFloodScopeState.Configured when snapshot.Value.Key?.IsEmpty == true => "без региона",
        NodeFloodScopeState.Configured => snapshot.Value.Name is { Length: > 0 } name ? name : "регион с пользовательским ключом",
        _ => "неизвестен",
    };
    public void ConfigureConnection(IConnectionSupervisor supervisor, ISessionCommandGateway? commands, IRegionStore? store)
    {
        _supervisor = supervisor; _commands = commands; _store = store;
        ConfigureDiscovery();
        SetDefaultCommand = new AsyncRelayCommand(() => _track(ExecuteAsync((node, context, token) => ChangeDefaultAsync(node, context, false, token))),
            () => CanChangeDefault && SelectedDefaultRegion is { Archived: false });
        ClearDefaultCommand = new AsyncRelayCommand(() => _track(ExecuteAsync((node, context, token) => ChangeDefaultAsync(node, context, true, token))), () => CanChangeDefault);
        supervisor.StateChanged += OnConnectionChanged;
    }
    private void OnConnectionChanged(object? sender, ConnectionSupervisorStateChangedEventArgs e)
    {
        if (_stopped || !_initialized || _service is null) return;
        _ = _track(_dispatcher.InvokeAsync(() =>
        {
            if (_stopped) return;
            NotifyConnection();
            if (_initialized) _ = _track(RefreshAsync());
        }));
    }
    private async Task ChangeDefaultAsync(Guid node, long context, bool clear, CancellationToken token)
    {
        if (!IsOnline || _commands is null) return;
        var definition = SelectedDefaultRegion?.Definition;
        var owner = CaptureOwner(node);
        var result = clear ? await _service!.ClearNodeDefaultAsync(owner, token) : await _service!.SetNodeDefaultAsync(owner,
            definition ?? throw new InvalidOperationException("Select a region."), token);
        if (result.Outcome != RegionDefaultChangeOutcome.Applied)
            await ShowErrorAsync(context, result.Outcome switch
            {
                RegionDefaultChangeOutcome.Rejected => "Нода отклонила изменение региона.",
                RegionDefaultChangeOutcome.ReadbackMismatch => "Прочитанное значение отличается от выбранного. Показано фактическое наблюдение.",
                RegionDefaultChangeOutcome.PersistenceFailed => "Изменение могло примениться на ноде, но не сохранено локально. Проверьте ошибку хранилища.",
                _ => "Результат изменения региона не подтверждён. Автоматического повтора нет; переподключитесь для чтения состояния.",
            });
    }
    private SessionCommandOwner CaptureOwner(Guid node)
    {
        var snapshot = _supervisor!.Snapshot;
        if (snapshot.State != ConnectionSupervisorState.Online || snapshot.NodeId != node || snapshot.SessionId is not { } session)
            throw new InvalidOperationException("The command session changed.");
        return new(node, session, snapshot.Generation, snapshot.SenderName ?? "", _commands?.GetRegionCapabilities(node), _commands?.GetDefaultRegion(node));
    }
    private void NotifyConnection()
    {
        OnPropertyChanged(nameof(IsOnline)); OnPropertyChanged(nameof(CanChangeDefault)); OnPropertyChanged(nameof(DefaultStatus));
        SetDefaultCommand?.NotifyCanExecuteChanged(); ClearDefaultCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSearch)); SearchCommand?.NotifyCanExecuteChanged(); AddFoundCommand?.NotifyCanExecuteChanged();
    }
}
