#if WINDOWS
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace MeshCoreMessenger.Desktop.Notifications;

internal sealed class WindowsNotificationPlatform : IDesktopNotificationPlatform
{
    private readonly TaskCompletionSource<string> _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, uint> _ids = [];
    private AppNotificationManager? _manager;
    private bool _registered;
    private bool _registrationAttempted;
    private bool _stopped;
    public NotificationPlatformStatus Status { get; private set; } = NotificationPlatformStatus.Initializing;
    public NotificationPlatformFailure? Failure { get; private set; }
    public event EventHandler? StatusChanged;
    public event EventHandler<string>? Activated;
    public Task InitializeAsync(CancellationToken token = default)
    {
        if (_stopped) return Task.CompletedTask;
        if (_registered) { RefreshStatus(); StatusChanged?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        if (_registrationAttempted) return Task.CompletedTask;
        _registrationAttempted = true;
        var stage = "CreateManager";
        try
        {
            _manager = AppNotificationManager.Default;
            stage = "SubscribeActivation";
            _manager.NotificationInvoked += OnActivated;
            stage = "Register";
            _manager.Register();
            _registered = true;
            stage = "ReadSettings";
            RefreshStatus();
        }
        catch (Exception error)
        {
            Failure = new(stage, error.GetType().Name, error.HResult);
            Console.Error.WriteLine($"Windows notification initialization failed: {stage}, {Failure.ErrorType}, HRESULT 0x{Failure.HResult:X8}.");
            if (_manager is not null) { try { _manager.NotificationInvoked -= OnActivated; } catch { } }
            Status = NotificationPlatformStatus.Unavailable;
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
    private void RefreshStatus() => Status = _manager!.Setting == AppNotificationSetting.Enabled
        ? NotificationPlatformStatus.Enabled : NotificationPlatformStatus.Denied;
    private void OnActivated(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (_stopped) return;
        var value = ParseToken(args);
        if (value is null) return;
        _startup.TrySetResult(value); Activated?.Invoke(this, value);
    }
    private static string? ParseToken(AppNotificationActivatedEventArgs args)
    {
        var values = args.Argument.Split('&').Select(item => item.Split('=', 2));
        var value = values.FirstOrDefault(item => item.Length == 2 && item[0] == "mcm")?[1];
        return Guid.TryParseExact(value, "N", out _) ? value : null;
    }
    public async Task<string?> GetStartupTokenAsync(string[] args, CancellationToken token = default)
    {
        if (_startup.Task.IsCompletedSuccessfully) return await _startup.Task.ConfigureAwait(false);
        if (!_registered || !args.Any(argument => argument.Contains("AppNotificationActivated", StringComparison.Ordinal))) return null;
        try
        {
            // A cold COM activation can be stored by the SDK rather than raised through NotificationInvoked.
            // Registration above must precede this read. The SDK wait runs away from UI/bootstrap's thread.
            return await Task.Run(() =>
            {
                var activated = AppInstance.GetCurrent().GetActivatedEventArgs();
                return activated.Kind == ExtendedActivationKind.AppNotification && activated.Data is AppNotificationActivatedEventArgs notification
                    ? ParseToken(notification) : null;
            }, token).WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return null; } // Missing/late native activation still permits normal startup.
    }
    public Task RequestPermissionAsync(CancellationToken token) => Task.CompletedTask;
    public Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        if (!_registered || _stopped) return Task.CompletedTask;
        RefreshStatus(); StatusChanged?.Invoke(this, EventArgs.Empty);
        if (Status != NotificationPlatformStatus.Enabled) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        var identifier = request.CoalescingKey ?? throw new ArgumentException("Native notification identifier is required.");
        var notification = new AppNotificationBuilder().AddArgument("mcm", identifier)
            .AddText(request.Title).AddText(request.Body).BuildNotification();
        notification.Tag = identifier[..16];
        _manager!.Show(notification);
        lock (_ids) _ids[identifier] = notification.Id;
        if (cancellationToken.IsCancellationRequested || _stopped) _ = RemoveAsync(identifier);
        return Task.CompletedTask;
    }
    public async Task RemoveAsync(string identifier)
    {
        uint id;
        lock (_ids) { if (!_ids.Remove(identifier, out id)) return; }
        await _manager!.RemoveByIdAsync(id);
    }
    public ValueTask DisposeAsync()
    {
        if (_stopped) return ValueTask.CompletedTask;
        _stopped = true;
        if (_manager is not null) _manager.NotificationInvoked -= OnActivated;
        if (_registered) { _manager!.Unregister(); _registered = false; }
        return ValueTask.CompletedTask;
    }
}
#endif
