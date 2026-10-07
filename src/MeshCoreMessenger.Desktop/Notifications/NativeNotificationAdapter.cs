using MeshCoreMessenger.Core.Application;

namespace MeshCoreMessenger.Desktop.Notifications;

internal sealed class NativeNotificationAdapter(IDesktopNotificationPlatform platform, NotificationTargetRegistry targets,
    IAppPaths paths) : IDesktopNotificationAdapter, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _tokens = [];
    private bool _stopped;
    public async Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        lock (_gate) if (_stopped) return;
        await platform.InitializeAsync(cancellationToken).ConfigureAwait(false); // Refresh OS permission without requesting it.
        if (platform.Status != NotificationPlatformStatus.Enabled) return;
        var key = request.CoalescingKey ?? Guid.NewGuid().ToString("N");
        string? old = null;
        lock (_gate)
        {
            if (_stopped) return;
            if (_tokens.Remove(key, out var previous)) old = previous;
            else if (_tokens.Count >= NotificationTargetRegistry.Capacity)
            { var first = _tokens.First(); _tokens.Remove(first.Key); old = first.Value; }
        }
        if (old is not null) { await RemoveSafelyAsync(old).ConfigureAwait(false); }
        cancellationToken.ThrowIfCancellationRequested();
        var token = targets.Add(paths.DataDirectory, request.Target);
        lock (_gate)
        {
            if (_stopped) { targets.Remove(token); return; }
            _tokens[key] = token;
        }
        try { await platform.ShowAsync(request with { CoalescingKey = token }, cancellationToken).ConfigureAwait(false); }
        catch
        {
            await RemoveSafelyAsync(token).ConfigureAwait(false);
            lock (_gate) if (_tokens.GetValueOrDefault(key) == token) _tokens.Remove(key);
            throw;
        }
    }
    public async Task StopAsync()
    {
        string[] tokens;
        lock (_gate) { if (_stopped) return; _stopped = true; tokens = _tokens.Values.ToArray(); _tokens.Clear(); }
        foreach (var token in tokens)
        {
            await RemoveSafelyAsync(token).ConfigureAwait(false);
        }
        try { await platform.DisposeAsync().ConfigureAwait(false); } catch { /* Native cleanup must not block durable shutdown. */ }
    }
    private async Task RemoveSafelyAsync(string token)
    {
        try { await platform.RemoveAsync(token).ConfigureAwait(false); }
        catch { /* Delivery is best-effort; target cleanup still runs. */ }
        finally { targets.Remove(token); }
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
