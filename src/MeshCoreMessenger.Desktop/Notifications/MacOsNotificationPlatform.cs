using System.Runtime.InteropServices;

namespace MeshCoreMessenger.Desktop.Notifications;

internal sealed class MacOsNotificationPlatform : IDesktopNotificationPlatform
{
    private readonly Native.Click _click;
    private GCHandle _clickRoot;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _startup = new();
    private bool _initialized;
    private bool _stopped;
    public NotificationPlatformStatus Status { get; private set; } = NotificationPlatformStatus.Initializing;
    public event EventHandler? StatusChanged;
    public event EventHandler<string>? Activated;
    public MacOsNotificationPlatform() => _click = value =>
    {
        if (!_stopped && Marshal.PtrToStringUTF8(value) is { } token)
        {
            if (Activated is { } handler) handler.Invoke(this, token); else _startup.Enqueue(token);
        }
    };
    public async Task InitializeAsync(CancellationToken token = default)
    {
        if (_stopped) return;
        if (_initialized)
        {
            if (_clickRoot.IsAllocated) SetStatus(Map(await InvokeAsync(Native.Status).WaitAsync(token).ConfigureAwait(false)));
            return;
        }
        _initialized = true;
        try
        {
            _clickRoot = GCHandle.Alloc(_click);
            if (Native.Init(_click) == 0) { _clickRoot.Free(); SetStatus(NotificationPlatformStatus.Unsupported); return; }
            SetStatus(Map(await InvokeAsync(Native.Status).WaitAsync(token).ConfigureAwait(false)));
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { if (_clickRoot.IsAllocated) _clickRoot.Free(); SetStatus(NotificationPlatformStatus.Unavailable); }
    }
    public async Task RequestPermissionAsync(CancellationToken token)
    {
        if (Status != NotificationPlatformStatus.PermissionRequired || _stopped) return;
        SetStatus(Map(await InvokeAsync(Native.Authorize).WaitAsync(token).ConfigureAwait(false)));
    }
    public async Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        if (_stopped || Status is NotificationPlatformStatus.Unsupported or NotificationPlatformStatus.Unavailable) return;
        SetStatus(Map(await InvokeAsync(Native.Status).WaitAsync(cancellationToken).ConfigureAwait(false)));
        if (Status != NotificationPlatformStatus.Enabled) return;
        cancellationToken.ThrowIfCancellationRequested();
        var identifier = request.CoalescingKey ?? throw new ArgumentException("Native notification identifier is required.");
        try
        {
            var result = await InvokeAsync(callback => Native.Show(identifier, request.Title, request.Body, callback))
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_stopped) Native.Remove(identifier);
            if (result != 1) throw new InvalidOperationException("macOS notification delivery failed.");
        }
        catch (OperationCanceledException) { Native.Remove(identifier); throw; }
    }
    public Task RemoveAsync(string identifier)
    {
        if (Status is not (NotificationPlatformStatus.Unsupported or NotificationPlatformStatus.Unavailable or NotificationPlatformStatus.Initializing)) Native.Remove(identifier);
        return Task.CompletedTask;
    }
    public Task<string?> GetStartupTokenAsync(string[] args, CancellationToken token = default) => Task.FromResult(_startup.TryDequeue(out var value) ? value : null);
    public ValueTask DisposeAsync()
    {
        if (_stopped) return ValueTask.CompletedTask;
        _stopped = true;
        if (_clickRoot.IsAllocated) Native.Stop();
        // Native delegate callbacks can already be queued; retain the one process-lifetime click thunk.
        return ValueTask.CompletedTask;
    }
    private void SetStatus(NotificationPlatformStatus value)
    { if (!_stopped && Status != value) { Status = value; StatusChanged?.Invoke(this, EventArgs.Empty); } }
    private static NotificationPlatformStatus Map(int value) => value switch
    { 0 => NotificationPlatformStatus.PermissionRequired, 1 => NotificationPlatformStatus.Enabled, 2 => NotificationPlatformStatus.Denied, _ => NotificationPlatformStatus.Unavailable };
    private static Task<int> InvokeAsync(Action<Native.Result> invoke)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        GCHandle root = default;
        Native.Result callback = result => { completion.TrySetResult(result); if (root.IsAllocated) root.Free(); };
        root = GCHandle.Alloc(callback);
        try { invoke(callback); } catch { root.Free(); throw; }
        return completion.Task;
    }
    private static class Native
    {
        private const string Library = "libMcmNotifications.dylib";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Click(IntPtr token);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Result(int status);
        [DllImport(Library, EntryPoint = "mcm_notifications_init")] internal static extern int Init(Click callback);
        [DllImport(Library, EntryPoint = "mcm_notifications_status")] internal static extern void Status(Result callback);
        [DllImport(Library, EntryPoint = "mcm_notifications_authorize")] internal static extern void Authorize(Result callback);
        [DllImport(Library, EntryPoint = "mcm_notifications_show")] internal static extern void Show(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string token, [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string body, Result callback);
        [DllImport(Library, EntryPoint = "mcm_notifications_remove")] internal static extern void Remove([MarshalAs(UnmanagedType.LPUTF8Str)] string token);
        [DllImport(Library, EntryPoint = "mcm_notifications_stop")] internal static extern void Stop();
    }
}
