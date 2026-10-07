using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshCoreMessenger.Desktop.Lifecycle;

namespace MeshCoreMessenger.Desktop.Notifications;

public sealed class NotificationSettingsViewModel : ObservableObject
{
    private readonly IDesktopNotificationPlatform _platform;
    private readonly IUiDispatcher _dispatcher;
    private string _statusText;
    private bool _stopped;
    internal NotificationSettingsViewModel(IDesktopNotificationPlatform platform, IUiDispatcher dispatcher)
    {
        _platform = platform; _dispatcher = dispatcher; _statusText = Describe(platform.Status);
        RequestPermissionCommand = new AsyncRelayCommand(RequestPermissionAsync, () => CanRequestPermission);
        platform.StatusChanged += OnStatusChanged;
    }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool CanRequestPermission => !_stopped && _platform.Status == NotificationPlatformStatus.PermissionRequired;
    public IAsyncRelayCommand RequestPermissionCommand { get; }
    private async Task RequestPermissionAsync(CancellationToken token)
    {
        try { await _platform.RequestPermissionAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { if (!_stopped) StatusText = Describe(NotificationPlatformStatus.Unavailable); }
    }
    private async void OnStatusChanged(object? sender, EventArgs args)
    {
        // This callback only updates UI; persistence and native delivery are owned elsewhere.
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (_stopped) return;
                StatusText = Describe(_platform.Status);
                OnPropertyChanged(nameof(CanRequestPermission)); RequestPermissionCommand.NotifyCanExecuteChanged();
            });
        }
        catch (Exception) { /* The dispatcher may already be shutting down. */ }
    }
    internal void Stop()
    {
        _stopped = true; _platform.StatusChanged -= OnStatusChanged; RequestPermissionCommand.Cancel();
        RequestPermissionCommand.NotifyCanExecuteChanged();
    }
    private static string Describe(NotificationPlatformStatus status) => status switch
    {
        NotificationPlatformStatus.Enabled => "Системные уведомления включены. Их показ зависит от настроек ОС и режима фокусирования.",
        NotificationPlatformStatus.PermissionRequired => "Разрешите приложению показывать уведомления в системе.",
        NotificationPlatformStatus.Denied => "Уведомления запрещены в системе. Разрешите их в настройках уведомлений ОС.",
        NotificationPlatformStatus.Unsupported => OperatingSystem.IsMacOS()
            ? "Для системных уведомлений на macOS запустите пакет MeshCoreMessenger.app." : "Системные уведомления на этой платформе пока не поддерживаются.",
        NotificationPlatformStatus.Unavailable => "Системные уведомления недоступны. Приём сообщений продолжает работать.",
        _ => "Проверка доступности системных уведомлений…",
    };
}
