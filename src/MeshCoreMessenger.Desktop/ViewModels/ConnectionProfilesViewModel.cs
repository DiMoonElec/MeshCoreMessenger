using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Profile directory/editor orchestration; connection lifecycle belongs to Control.</summary>
public sealed class ConnectionProfilesViewModel : ConnectionProfileEditorViewModel
{
    private readonly IConnectionProfileManager _manager;
    private readonly ISerialPortCatalog _serialPorts;
    private readonly ILogger<ConnectionProfilesViewModel> _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private ConnectionProfileListItem? _selectedProfile;
    private ConnectionProfileListItem? _pendingProfile;
    private bool _pendingNew;
    private bool _hasPendingSelection;
    private bool _isBusy;
    private string _status = "Профиль подключения не выбран";
    private string? _errorMessage;
    private HashSet<string> _detected = [];
    private int _stopped;
    public ConnectionProfilesViewModel(IConnectionProfileManager manager, IConnectionSupervisor supervisor,
        ISerialPortCatalog serialPorts, ILogger<ConnectionProfilesViewModel> logger,
        IUiDispatcher? dispatcher = null, INodeStore? nodes = null,
        TimeProvider? time = null, TimeSpan? disconnectTimeout = null)
    {
        _manager = manager; _serialPorts = serialPorts; _logger = logger;
        Control = new ConnectionControlViewModel(this, manager, supervisor, dispatcher, nodes, time, disconnectTimeout);
        NewProfileCommand = new RelayCommand(() => RequestSelection(null, true), () => CanEdit);
        RefreshPortsCommand = new AsyncRelayCommand(RefreshPortsAsync, () => CanEdit);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave);
        CancelCommand = new RelayCommand(() => { LoadEditor(SavedProfile); ErrorMessage = null; }, () => IsDirty && CanEdit);
        DiscardAndSwitchCommand = new RelayCommand(ApplyPendingSelection, () => CanEdit);
        StayCommand = new RelayCommand(ClearPendingSelection, () => CanEdit);
        SaveAndSwitchCommand = new AsyncRelayCommand(async token =>
        {
            if (!CanSave) return;
            await SaveAsync(token);
            if (CanEdit && !IsDirty && ErrorMessage is null) ApplyPendingSelection();
        }, () => CanSave);
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(IsDirty) or nameof(IsValid) or nameof(IsBusy))
            {
                NotifyControlChanged(); Control.RefreshActions();
            }
            if (args.PropertyName == nameof(SerialPortName)) OnPropertyChanged(nameof(SerialPortNotice));
            if (args.PropertyName == nameof(SavedProfile)) Control.RefreshActions();
        };
    }
    public ConnectionControlViewModel Control { get; }
    public ObservableCollection<ConnectionProfileListItem> AvailableProfiles { get; } = [];
    public ObservableCollection<string> AvailableSerialPorts { get; } = [];
    public IReadOnlyList<ConnectionTransportKind> TransportKinds { get; } = [ConnectionTransportKind.Serial, ConnectionTransportKind.Tcp];
    public IRelayCommand NewProfileCommand { get; }
    public IAsyncRelayCommand RefreshPortsCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand DiscardAndSwitchCommand { get; }
    public IRelayCommand StayCommand { get; }
    public IAsyncRelayCommand SaveAndSwitchCommand { get; }
    public bool CanSave => IsDirty && IsValid && CanEdit;
    public bool HasPendingSelection => _hasPendingSelection;
    public string? SerialPortNotice => string.IsNullOrWhiteSpace(SerialPortName) || _detected.Contains(SerialPortName)
        ? null : "Порт сейчас не обнаружен. Сохранённое значение не изменено.";
    public ConnectionProfileListItem? SelectedProfile
    {
        get => _selectedProfile;
        set { if (!ReferenceEquals(value, _selectedProfile)) RequestSelection(value, false); }
    }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string? ErrorMessage { get => _errorMessage; private set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => ErrorMessage is not null;
    public bool CanEdit => !IsBusy && !Control.IsBusy && Volatile.Read(ref _stopped) == 0;
    internal void NotifyControlChanged()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanSave));
        SaveCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        SaveAndSwitchCommand.NotifyCanExecuteChanged(); NewProfileCommand.NotifyCanExecuteChanged(); RefreshPortsCommand.NotifyCanExecuteChanged();
        DiscardAndSwitchCommand.NotifyCanExecuteChanged(); StayCommand.NotifyCanExecuteChanged();
    }
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        IsBusy = true;
        try
        {
            var profiles = await _manager.GetProfilesAsync(linked.Token);
            var selected = await _manager.GetSelectedProfileAsync(linked.Token);
            ReplaceProfiles(profiles, selected?.Id);
            await RefreshPortsCoreAsync(linked.Token);
            await Control.LoadAsync(linked.Token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { _logger.LogError(error, "Could not load connection profiles."); ErrorMessage = "Не удалось загрузить профили подключения."; }
        finally { IsBusy = false; }
    }
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _lifetime.Cancel(); SaveCommand.Cancel(); RefreshPortsCommand.Cancel(); SaveAndSwitchCommand.Cancel();
        await Control.StopAsync();
        try { await Task.WhenAll(new[] { SaveCommand.ExecutionTask, RefreshPortsCommand.ExecutionTask, SaveAndSwitchCommand.ExecutionTask }.OfType<Task>()); }
        catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }
    private async Task SaveAsync(CancellationToken token)
    {
        if (!CanSave) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        IsBusy = true; ErrorMessage = null;
        try
        {
            var saved = await _manager.SaveAsync(CreateDraft(), linked.Token);
            var existing = AvailableProfiles.FirstOrDefault(item => item.Id == saved.Id);
            if (existing is not null) AvailableProfiles.Remove(existing);
            var item = new ConnectionProfileListItem(saved); AvailableProfiles.Add(item);
            _selectedProfile = item; OnPropertyChanged(nameof(SelectedProfile)); LoadEditor(saved);
            Status = "Профиль сохранён";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { _logger.LogWarning(error, "Could not save profile."); ErrorMessage = error is ArgumentException ? error.Message : "Не удалось сохранить профиль."; }
        finally { IsBusy = false; }
    }
    private async Task RefreshPortsAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        IsBusy = true; ErrorMessage = null;
        try { await RefreshPortsCoreAsync(linked.Token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { _logger.LogWarning(error, "Could not enumerate ports."); ErrorMessage = "Не удалось обновить список Serial-портов. Предыдущий список сохранён."; }
        finally { IsBusy = false; }
    }
    private async Task RefreshPortsCoreAsync(CancellationToken token)
    {
        var detected = await _serialPorts.GetPortNamesAsync(token);
        _detected = detected.ToHashSet(StringComparer.Ordinal);
        var all = detected.Concat(AvailableProfiles.Select(item => item.Profile.SerialPortName).OfType<string>())
            .Append(SerialPortName).Where(port => !string.IsNullOrWhiteSpace(port)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        AvailableSerialPorts.Clear(); foreach (var port in all) AvailableSerialPorts.Add(port);
        // Reapply the editor selection after ComboBox lost it during the collection reset.
        OnPropertyChanged(nameof(SerialPortName));
        OnPropertyChanged(nameof(SerialPortNotice));
    }
    private void ReplaceProfiles(IReadOnlyList<ConnectionProfile> profiles, Guid? selected)
    {
        AvailableProfiles.Clear(); foreach (var profile in profiles) AvailableProfiles.Add(new(profile));
        SetSelection(AvailableProfiles.FirstOrDefault(item => item.Id == selected));
    }
    private void RequestSelection(ConnectionProfileListItem? profile, bool isNew)
    {
        if (!CanEdit) { OnPropertyChanged(nameof(SelectedProfile)); return; }
        if (IsDirty)
        {
            _pendingProfile = profile; _pendingNew = isNew; _hasPendingSelection = true;
            OnPropertyChanged(nameof(HasPendingSelection)); OnPropertyChanged(nameof(SelectedProfile)); Control.RefreshActions(); return;
        }
        SetSelection(profile);
        if (isNew) Status = "Новый профиль";
    }
    private void SetSelection(ConnectionProfileListItem? profile)
    {
        _selectedProfile = profile; OnPropertyChanged(nameof(SelectedProfile)); LoadEditor(profile?.Profile); ErrorMessage = null;
        Status = profile is null ? "Профиль подключения не выбран" : $"Выбран профиль: {profile.Profile.Name}";
    }
    private void ApplyPendingSelection() { SetSelection(_pendingNew ? null : _pendingProfile); ClearPendingSelection(); }
    private void ClearPendingSelection() { _pendingProfile = null; _pendingNew = false; _hasPendingSelection = false; OnPropertyChanged(nameof(HasPendingSelection)); Control.RefreshActions(); }
}

public sealed class ConnectionProfileListItem(ConnectionProfile profile)
{
    public ConnectionProfile Profile { get; } = profile;
    public Guid Id => Profile.Id;
    public string DisplayName => $"{Profile.Name} — {Profile.Transport}";
}
