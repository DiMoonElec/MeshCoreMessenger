using MeshCoreMessenger.Desktop.Notifications;

namespace MeshCoreMessenger.Desktop.Lifecycle;

/// <summary>Queues activation before the first window opens; never owns connection or UI state.</summary>
internal sealed class DesktopActivationCoordinator(IUiDispatcher dispatcher) : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<Func<bool>> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<bool>? _pending;
    private readonly TaskCompletionSource<Func<NotificationTarget, CancellationToken, Task>> _navigation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    private bool _disposed;

    public void Attach(Func<bool> activate) => _ready.TrySetResult(activate);

    public Task<bool> RequestAsync(CancellationToken cancellationToken = default)
    {
        Task<bool> request;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(false);
            if (_pending is null || _pending.IsCompleted) _pending = ActivateAsync();
            request = _pending;
        }
        // One canceled caller must not cancel a request shared by other callers.
        return request.WaitAsync(cancellationToken);
    }

    internal void AttachNavigation(Func<NotificationTarget, CancellationToken, Task> navigate) => _navigation.TrySetResult(navigate);

    internal async Task<bool> RequestNotificationAsync(NotificationTarget target, CancellationToken cancellationToken = default)
    {
        CancellationToken stop;
        lock (_gate) { if (_disposed) return false; stop = _stop.Token; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, cancellationToken);
        if (!await RequestAsync(linked.Token).ConfigureAwait(false)) return false;
        if (target is OpenApplicationTarget) return true;
        var navigate = await _navigation.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        await _navigationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            Task operation = Task.CompletedTask;
            await dispatcher.InvokeAsync(() => operation = navigate(target, linked.Token), linked.Token).ConfigureAwait(false);
            await operation.ConfigureAwait(false); return true;
        }
        finally { _navigationGate.Release(); }
    }

    private async Task<bool> ActivateAsync()
    {
        try
        {
            var activate = await _ready.Task.WaitAsync(_stop.Token).ConfigureAwait(false);
            var result = false;
            await dispatcher.InvokeAsync(() =>
            {
                if (!_stop.IsCancellationRequested) result = activate();
            }, _stop.Token).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return false; }
    }

    public void Dispose()
    {
        Task<bool>? pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _pending;
        }
        _stop.Cancel();
        // Dispose only once a queued callback has observed cancellation.
        if (pending is null) _stop.Dispose();
        else _ = pending.ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
