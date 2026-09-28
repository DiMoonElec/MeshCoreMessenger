using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Desktop.ViewModels;

public sealed class ConnectionProfilesViewModel : ObservableObject
{
    private readonly IConnectionProfileManager _manager;
    private readonly IConnectionSupervisor _supervisor;
    private readonly ISerialPortCatalog _serialPorts;
    private readonly ILogger<ConnectionProfilesViewModel> _logger;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private ConnectionProfileListItem? _selectedProfile;
    private Guid? _editingId;
    private string _profileName = string.Empty;
    private ConnectionTransportKind _selectedTransport = ConnectionTransportKind.Serial;
    private string _tcpHost = "127.0.0.1";
    private string _tcpPort = "5000";
    private string _serialPortName = string.Empty;
    private string _baudRate = "115200";
    private bool _dtrEnable = true;
    private bool _rtsEnable = true;
    private string _openDelayMilliseconds = "2000";
    private string _commandTimeoutMilliseconds = "10000";
    private string _acknowledgementTimeoutMilliseconds = "30000";
    private bool _autoConnect;
    private bool _reconnect = true;
    private bool _isBusy;
    private string _status = "Профиль подключения не выбран";
    private string? _errorMessage;
    private int _stopped;

    public ConnectionProfilesViewModel(
        IConnectionProfileManager manager,
        IConnectionSupervisor supervisor,
        ISerialPortCatalog serialPorts,
        ILogger<ConnectionProfilesViewModel> logger)
    {
        _manager = manager;
        _supervisor = supervisor;
        _serialPorts = serialPorts;
        _logger = logger;
        NewProfileCommand = new RelayCommand(BeginNewProfile);
        RefreshPortsCommand = new AsyncRelayCommand(RefreshPortsAsync);
        SaveAndSelectCommand = new AsyncRelayCommand(SaveAndSelectAsync);
    }

    public ObservableCollection<ConnectionProfileListItem> AvailableProfiles { get; } = [];
    public ObservableCollection<string> AvailableSerialPorts { get; } = [];
    public IReadOnlyList<ConnectionTransportKind> TransportKinds { get; } =
        [ConnectionTransportKind.Tcp, ConnectionTransportKind.Serial];
    public IRelayCommand NewProfileCommand { get; }
    public IAsyncRelayCommand RefreshPortsCommand { get; }
    public IAsyncRelayCommand SaveAndSelectCommand { get; }

    public ConnectionProfileListItem? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                if (value is null)
                {
                    ResetEditor();
                }
                else
                {
                    LoadEditor(value.Profile);
                }
            }
        }
    }

    public string ProfileName
    {
        get => _profileName;
        set => SetProperty(ref _profileName, value);
    }

    public ConnectionTransportKind SelectedTransport
    {
        get => _selectedTransport;
        set
        {
            if (SetProperty(ref _selectedTransport, value))
            {
                OnPropertyChanged(nameof(IsTcp));
                OnPropertyChanged(nameof(IsSerial));
            }
        }
    }

    public bool IsTcp => SelectedTransport == ConnectionTransportKind.Tcp;
    public bool IsSerial => SelectedTransport == ConnectionTransportKind.Serial;

    public string TcpHost
    {
        get => _tcpHost;
        set => SetProperty(ref _tcpHost, value);
    }

    public string TcpPort
    {
        get => _tcpPort;
        set => SetProperty(ref _tcpPort, value);
    }

    public string SerialPortName
    {
        get => _serialPortName;
        set => SetProperty(ref _serialPortName, value);
    }

    public string BaudRate
    {
        get => _baudRate;
        set => SetProperty(ref _baudRate, value);
    }

    public bool DtrEnable
    {
        get => _dtrEnable;
        set => SetProperty(ref _dtrEnable, value);
    }

    public bool RtsEnable
    {
        get => _rtsEnable;
        set => SetProperty(ref _rtsEnable, value);
    }

    public string OpenDelayMilliseconds
    {
        get => _openDelayMilliseconds;
        set => SetProperty(ref _openDelayMilliseconds, value);
    }

    public string CommandTimeoutMilliseconds
    {
        get => _commandTimeoutMilliseconds;
        set => SetProperty(ref _commandTimeoutMilliseconds, value);
    }

    public string AcknowledgementTimeoutMilliseconds
    {
        get => _acknowledgementTimeoutMilliseconds;
        set => SetProperty(ref _acknowledgementTimeoutMilliseconds, value);
    }

    public bool AutoConnect
    {
        get => _autoConnect;
        set => SetProperty(ref _autoConnect, value);
    }

    public bool Reconnect
    {
        get => _reconnect;
        set => SetProperty(ref _reconnect, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => ErrorMessage is not null;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCancellation = CreateLinkedCancellation(cancellationToken);
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var profiles = await _manager.GetProfilesAsync(linkedCancellation.Token);
            var selected = await _manager.GetSelectedProfileAsync(linkedCancellation.Token);
            ApplyProfiles(profiles, selected?.Id);
            await RefreshPortsCoreAsync(profiles, linkedCancellation.Token);
            Status = selected is null
                ? "Профиль подключения не выбран"
                : $"Выбран профиль: {selected.Name}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not load connection profiles.");
            ErrorMessage = "Не удалось загрузить профили подключения.";
            Status = "Ошибка настроек подключения";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        RefreshPortsCommand.Cancel();
        SaveAndSelectCommand.Cancel();
        var pending = new[] { RefreshPortsCommand.ExecutionTask, SaveAndSelectCommand.ExecutionTask }
            .OfType<Task>()
            .ToArray();
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _lifetimeCancellation.Dispose();
    }

    private async Task SaveAndSelectAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CreateLinkedCancellation(cancellationToken);
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var saved = await _manager.SaveAndSelectAsync(CreateDraft(), linkedCancellation.Token);
            await _supervisor.SwitchProfileAsync(saved.Id, linkedCancellation.Token);
            var profiles = await _manager.GetProfilesAsync(linkedCancellation.Token);
            ApplyProfiles(profiles, saved.Id);
            await RefreshPortsCoreAsync(profiles, linkedCancellation.Token);
            Status = $"Выбран профиль: {saved.Name}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not save connection profile.");
            ErrorMessage = exception is ArgumentException
                ? exception.Message
                : "Не удалось сохранить профиль подключения.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshPortsAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CreateLinkedCancellation(cancellationToken);
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var profiles = AvailableProfiles.Select(item => item.Profile).ToArray();
            await RefreshPortsCoreAsync(profiles, linkedCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not enumerate serial ports.");
            ErrorMessage = "Не удалось обновить список Serial-портов.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshPortsCoreAsync(
        IReadOnlyList<ConnectionProfile> profiles,
        CancellationToken cancellationToken)
    {
        var detected = await _serialPorts.GetPortNamesAsync(cancellationToken);
        var all = detected
            .Concat(profiles.Select(item => item.SerialPortName).OfType<string>())
            .Append(SerialPortName)
            .Where(port => !string.IsNullOrWhiteSpace(port))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        AvailableSerialPorts.Clear();
        foreach (var port in all)
        {
            AvailableSerialPorts.Add(port);
        }
    }

    private ConnectionProfileDraft CreateDraft() => new()
    {
        Id = _editingId,
        Name = ProfileName,
        Transport = SelectedTransport,
        TcpHost = IsTcp ? TcpHost : null,
        TcpPort = IsTcp ? ParseInteger(TcpPort, "TCP port", minimum: 1, maximum: 65_535) : null,
        SerialPortName = IsSerial ? SerialPortName : null,
        BaudRate = IsSerial ? ParseInteger(BaudRate, "Baud rate", minimum: 1) : null,
        DtrEnable = DtrEnable,
        RtsEnable = RtsEnable,
        OpenDelayMilliseconds = ParseInteger(OpenDelayMilliseconds, "Open delay", minimum: 0),
        CommandTimeoutMilliseconds = ParseInteger(CommandTimeoutMilliseconds, "Command timeout", minimum: 1),
        AcknowledgementTimeoutMilliseconds = ParseInteger(
            AcknowledgementTimeoutMilliseconds,
            "ACK timeout",
            minimum: 1),
        AutoConnect = AutoConnect,
        Reconnect = Reconnect,
    };

    private static int ParseInteger(string value, string fieldName, int minimum, int maximum = int.MaxValue)
    {
        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
        {
            throw new ArgumentException($"{fieldName} должен быть числом от {minimum} до {maximum}.");
        }

        return parsed;
    }

    private void ApplyProfiles(IReadOnlyList<ConnectionProfile> profiles, Guid? selectedId)
    {
        AvailableProfiles.Clear();
        foreach (var profile in profiles)
        {
            AvailableProfiles.Add(new ConnectionProfileListItem(profile));
        }

        SelectedProfile = selectedId is { } id
            ? AvailableProfiles.FirstOrDefault(item => item.Id == id)
            : null;
    }

    private void BeginNewProfile()
    {
        SelectedProfile = null;
        ResetEditor();
        ErrorMessage = null;
        Status = "Новый профиль";
    }

    private void ResetEditor()
    {
        _editingId = null;
        ProfileName = string.Empty;
        SelectedTransport = ConnectionTransportKind.Serial;
        TcpHost = "127.0.0.1";
        TcpPort = "5000";
        SerialPortName = string.Empty;
        BaudRate = "115200";
        DtrEnable = true;
        RtsEnable = true;
        OpenDelayMilliseconds = "2000";
        CommandTimeoutMilliseconds = "10000";
        AcknowledgementTimeoutMilliseconds = "30000";
        AutoConnect = false;
        Reconnect = true;
    }

    private void LoadEditor(ConnectionProfile profile)
    {
        _editingId = profile.Id;
        ProfileName = profile.Name;
        SelectedTransport = profile.Transport;
        TcpHost = profile.TcpHost ?? "127.0.0.1";
        TcpPort = profile.TcpPort?.ToString() ?? "5000";
        SerialPortName = profile.SerialPortName ?? string.Empty;
        BaudRate = profile.BaudRate?.ToString() ?? "115200";
        DtrEnable = profile.DtrEnable;
        RtsEnable = profile.RtsEnable;
        OpenDelayMilliseconds = profile.OpenDelayMilliseconds.ToString();
        CommandTimeoutMilliseconds = profile.CommandTimeoutMilliseconds.ToString();
        AcknowledgementTimeoutMilliseconds = profile.AcknowledgementTimeoutMilliseconds.ToString();
        AutoConnect = profile.AutoConnect;
        Reconnect = profile.Reconnect;
    }

    private CancellationTokenSource CreateLinkedCancellation(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
}

public sealed class ConnectionProfileListItem
{
    public ConnectionProfileListItem(ConnectionProfile profile)
    {
        Profile = profile;
        Id = profile.Id;
        DisplayName = $"{profile.Name} — {profile.Transport}";
    }

    public Guid Id { get; }
    public string DisplayName { get; }
    public ConnectionProfile Profile { get; }
}
