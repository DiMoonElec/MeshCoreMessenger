using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>In-memory draft: no storage or transport ownership.</summary>
public partial class ConnectionProfileEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _profileName = string.Empty;
    [ObservableProperty] private ConnectionTransportKind _selectedTransport = ConnectionTransportKind.Serial;
    [ObservableProperty] private string _tcpHost = "127.0.0.1";
    [ObservableProperty] private string _tcpPort = "5000";
    [ObservableProperty] private string _serialPortName = string.Empty;
    [ObservableProperty] private string _baudRate = "115200";
    [ObservableProperty] private bool _dtrEnable = true;
    [ObservableProperty] private bool _rtsEnable = true;
    [ObservableProperty] private string _openDelayMilliseconds = "2000";
    [ObservableProperty] private string _commandTimeoutMilliseconds = "10000";
    [ObservableProperty] private string _acknowledgementTimeoutMilliseconds = "30000";
    [ObservableProperty] private bool _autoConnect;
    [ObservableProperty] private bool _reconnect = true;
    private string[] _baseline = [];
    private bool _loading;
    public ConnectionProfileEditorViewModel() => LoadEditor(null);
    public ConnectionProfile? SavedProfile { get; private set; }
    public bool IsTcp => SelectedTransport == ConnectionTransportKind.Tcp;
    public bool IsSerial => SelectedTransport == ConnectionTransportKind.Serial;
    public bool IsDirty => !Values().SequenceEqual(_baseline);
    public bool IsValid => new[] { ProfileNameError, TcpHostError, TcpPortError, SerialPortNameError,
        BaudRateError, OpenDelayError, CommandTimeoutError, AckTimeoutError }.All(error => error is null);
    public string? ProfileNameError => string.IsNullOrWhiteSpace(ProfileName) ? "Укажите название профиля." : null;
    public string? TcpHostError => IsTcp && string.IsNullOrWhiteSpace(TcpHost) ? "Укажите host или IP-адрес." : null;
    public string? SerialPortNameError => IsSerial && string.IsNullOrWhiteSpace(SerialPortName) ? "Укажите Serial-порт." : null;
    public string? TcpPortError => IsTcp ? NumberError(TcpPort, "TCP port", 1, 65535) : null;
    public string? BaudRateError => IsSerial ? NumberError(BaudRate, "Baud rate", 1) : null;
    public string? OpenDelayError => IsSerial ? NumberError(OpenDelayMilliseconds, "Задержка", 0) : null;
    public string? CommandTimeoutError => NumberError(CommandTimeoutMilliseconds, "Тайм-аут команды", 1);
    public string? AckTimeoutError => NumberError(AcknowledgementTimeoutMilliseconds, "Тайм-аут ACK", 1);

    protected override void OnPropertyChanged(PropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        if (!_loading && args.PropertyName is nameof(ProfileName) or nameof(SelectedTransport) or
            nameof(TcpHost) or nameof(TcpPort) or nameof(SerialPortName) or nameof(BaudRate) or
            nameof(DtrEnable) or nameof(RtsEnable) or nameof(OpenDelayMilliseconds) or
            nameof(CommandTimeoutMilliseconds) or nameof(AcknowledgementTimeoutMilliseconds) or
            nameof(AutoConnect) or nameof(Reconnect)) NotifyEditor();
    }
    private void NotifyEditor()
    {
        foreach (var name in new[] { nameof(ProfileNameError), nameof(TcpHostError), nameof(TcpPortError),
            nameof(SerialPortNameError), nameof(BaudRateError), nameof(OpenDelayError), nameof(CommandTimeoutError),
            nameof(AckTimeoutError), nameof(IsValid), nameof(IsDirty), nameof(IsTcp), nameof(IsSerial) }) OnPropertyChanged(name);
    }
    public void LoadEditor(ConnectionProfile? profile)
    {
        _loading = true; SavedProfile = profile;
        ProfileName = profile?.Name ?? string.Empty;
        SelectedTransport = profile?.Transport ?? ConnectionTransportKind.Serial;
        TcpHost = profile?.TcpHost ?? "127.0.0.1"; TcpPort = profile?.TcpPort?.ToString() ?? "5000";
        SerialPortName = profile?.SerialPortName ?? string.Empty; BaudRate = profile?.BaudRate?.ToString() ?? "115200";
        DtrEnable = profile?.DtrEnable ?? true; RtsEnable = profile?.RtsEnable ?? true;
        OpenDelayMilliseconds = (profile?.OpenDelayMilliseconds ?? 2000).ToString();
        CommandTimeoutMilliseconds = (profile?.CommandTimeoutMilliseconds ?? 10000).ToString();
        AcknowledgementTimeoutMilliseconds = (profile?.AcknowledgementTimeoutMilliseconds ?? 30000).ToString();
        AutoConnect = profile?.AutoConnect ?? false; Reconnect = profile?.Reconnect ?? true;
        _baseline = Values(); _loading = false; OnPropertyChanged(nameof(SavedProfile)); NotifyEditor();
    }
    public ConnectionProfileDraft CreateDraft()
    {
        if (!IsValid) throw new ArgumentException("Исправьте ошибки в полях профиля.");
        return new()
        {
            Id = SavedProfile?.Id, Name = ProfileName.Trim(), Transport = SelectedTransport,
            TcpHost = IsTcp ? TcpHost.Trim() : null, TcpPort = IsTcp ? int.Parse(TcpPort) : null,
            SerialPortName = IsSerial ? SerialPortName.Trim() : null, BaudRate = IsSerial ? int.Parse(BaudRate) : null,
            DtrEnable = DtrEnable, RtsEnable = RtsEnable,
            OpenDelayMilliseconds = IsSerial ? int.Parse(OpenDelayMilliseconds) : SavedProfile?.OpenDelayMilliseconds ?? 2000,
            CommandTimeoutMilliseconds = int.Parse(CommandTimeoutMilliseconds),
            AcknowledgementTimeoutMilliseconds = int.Parse(AcknowledgementTimeoutMilliseconds),
            AutoConnect = AutoConnect, Reconnect = Reconnect,
        };
    }
    public static bool SameSettings(ConnectionProfile left, ConnectionProfile right) =>
        left with { CreatedUtc = right.CreatedUtc, UpdatedUtc = right.UpdatedUtc } == right;
    private static string? NumberError(string text, string name, int minimum, int maximum = int.MaxValue) =>
        int.TryParse(text, out var value) && value >= minimum && value <= maximum ? null :
            $"{name}: целое число от {minimum} до {maximum}.";
    private string[] Values() => [ProfileName, SelectedTransport.ToString(), TcpHost, TcpPort, SerialPortName,
        BaudRate, DtrEnable.ToString(), RtsEnable.ToString(), OpenDelayMilliseconds, CommandTimeoutMilliseconds,
        AcknowledgementTimeoutMilliseconds, AutoConnect.ToString(), Reconnect.ToString()];
}
