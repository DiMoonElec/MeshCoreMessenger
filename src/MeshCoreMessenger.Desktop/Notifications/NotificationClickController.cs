using System.Diagnostics;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Platform;
using Microsoft.Extensions.Logging;

namespace MeshCoreMessenger.Desktop.Notifications;

internal sealed class NotificationClickController(IDesktopNotificationPlatform platform, NotificationTargetRegistry targets,
    IAppPaths paths, DesktopActivationCoordinator activation, IUiDispatcher dispatcher, ILogger<NotificationClickController> logger) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<Task> _pending = [];
    private readonly object _gate = new();
    private bool _started;
    private bool _stopped;
    public void Start(NotificationTarget? startupTarget = null)
    {
        lock (_gate)
        {
            if (_started || _stopped) return;
            _started = true; platform.Activated += OnActivated;
            if (startupTarget is not null) Track(() => activation.RequestNotificationAsync(startupTarget, _stop.Token));
            else Track(async () => { var token = await platform.GetStartupTokenAsync([]); if (token is not null) await HandleAsync(token); });
        }
    }
    private void OnActivated(object? sender, string token)
    { lock (_gate) { if (!_stopped) Track(() => HandleAsync(token)); } }
    private void Track(Func<Task> operation)
    {
        var task = RunAsync(operation); _pending.Add(task);
        _ = task.ContinueWith(_ => { lock (_gate) _pending.Remove(task); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private async Task RunAsync(Func<Task> operation)
    {
        try { await operation().ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { logger.LogWarning("Notification activation failed ({ErrorType}).", error.GetType().Name); }
    }
    private async Task HandleAsync(string token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var entry = targets.Get(token);
        if (entry is null) { await activation.RequestAsync(deadline.Token); return; }
        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Path.GetFullPath(paths.DataDirectory), entry.DataDirectory, comparer))
        { await activation.RequestNotificationAsync(entry.Target, deadline.Token); return; }
        if (!Directory.Exists(entry.DataDirectory)) { await activation.RequestAsync(deadline.Token); return; }
        using var forwarding = new DesktopActivationCoordinator(dispatcher);
        var owner = await ApplicationInstanceCoordinator.AcquireOrActivateAsync(DesktopAppPaths.CreateForDirectory(entry.DataDirectory),
            forwarding, cancellationToken: deadline.Token, notificationTarget: entry.Target as MessageNotificationTarget);
        if (owner is null) return;
        owner.Dispose(); // No DB/DI/session is created by the temporary probe.
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Application executable is unavailable.");
        ProcessStartInfo start;
        var bundleEnd = executable.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
        if (OperatingSystem.IsMacOS() && bundleEnd >= 0)
        {
            start = new("/usr/bin/open") { UseShellExecute = false };
            start.ArgumentList.Add("-n"); start.ArgumentList.Add(executable[..(bundleEnd + 4)]); start.ArgumentList.Add("--args");
        }
        else
        {
            start = new(executable) { UseShellExecute = false };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(App).Assembly.Location);
        }
        start.ArgumentList.Add("--notification-token"); start.ArgumentList.Add(token);
        Process.Start(start)?.Dispose();
    }
    public async Task StopAsync()
    {
        Task[] tasks;
        lock (_gate)
        {
            if (!_stopped) { _stopped = true; platform.Activated -= OnActivated; _stop.Cancel(); }
            tasks = _pending.ToArray();
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
