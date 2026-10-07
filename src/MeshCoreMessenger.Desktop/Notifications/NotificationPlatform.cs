namespace MeshCoreMessenger.Desktop.Notifications;

internal enum NotificationPlatformStatus { Initializing, Enabled, PermissionRequired, Denied, Unsupported, Unavailable }
internal sealed record NotificationPlatformFailure(string Stage, string ErrorType, int HResult);

internal interface IDesktopNotificationPlatform : IDesktopNotificationAdapter, IAsyncDisposable
{
    NotificationPlatformStatus Status { get; }
    NotificationPlatformFailure? Failure => null;
    event EventHandler? StatusChanged;
    event EventHandler<string>? Activated;
    Task InitializeAsync(CancellationToken token = default);
    Task RequestPermissionAsync(CancellationToken token);
    Task RemoveAsync(string identifier);
    Task<string?> GetStartupTokenAsync(string[] args, CancellationToken token = default);
}

internal static class NotificationPlatformFactory
{
    public static IDesktopNotificationPlatform Create()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows()) return new WindowsNotificationPlatform();
#endif
        if (OperatingSystem.IsMacOS()) return new MacOsNotificationPlatform();
        return new UnsupportedNotificationPlatform(); // Linux deferred.
    }
}
internal sealed class UnsupportedNotificationPlatform : IDesktopNotificationPlatform
{
    public NotificationPlatformStatus Status => NotificationPlatformStatus.Unsupported;
    public event EventHandler? StatusChanged { add { } remove { } }
    public event EventHandler<string>? Activated { add { } remove { } }
    public Task InitializeAsync(CancellationToken token = default) => Task.CompletedTask;
    public Task RequestPermissionAsync(CancellationToken token) => Task.CompletedTask;
    public Task RemoveAsync(string identifier) => Task.CompletedTask;
    public Task<string?> GetStartupTokenAsync(string[] args, CancellationToken token = default) => Task.FromResult<string?>(null);
    public Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
