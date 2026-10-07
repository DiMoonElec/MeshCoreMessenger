using Microsoft.Extensions.Logging;

namespace MeshCoreMessenger.Desktop.Notifications;

public abstract record NotificationTarget;
public sealed record OpenApplicationTarget : NotificationTarget;
public sealed record MessageNotificationTarget(Guid NodeId, Guid ConversationId, Guid MessageId) : NotificationTarget;

/// <summary>A ready notification; targets carry identities, never executable callbacks or launch text.</summary>
public record NotificationRequest(string Title, string Body, NotificationTarget? Target = null, string? CoalescingKey = null);

public interface IDesktopNotificationService
{
    bool Submit(NotificationRequest request);
}

internal interface IDesktopNotificationAdapter
{
    // Implementations must honor cancellation before delivering a banner.
    Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken);
}

internal interface INotificationRequestPolicy
{
    Task<NotificationRequest?> PrepareAsync(NotificationRequest request, CancellationToken cancellationToken);
}

internal sealed class UnavailableNotificationAdapter : IDesktopNotificationAdapter
{
    public Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Bounded transient delivery, independent of messages, storage, and connection policy.</summary>
internal sealed class DesktopNotificationService : IDesktopNotificationService, IAsyncDisposable
{
    internal const int Capacity = 128;
    private readonly object _gate = new();
    private readonly Dictionary<string, NotificationRequest> _pending = [];
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly IDesktopNotificationAdapter _adapter;
    private readonly IEnumerable<INotificationRequestPolicy> _policies;
    private readonly ILogger<DesktopNotificationService> _logger;
    private readonly Task _worker;
    private bool _stopped;
    private NotificationRequest? _overflow;
    private Task? _stopping;

    public DesktopNotificationService(IDesktopNotificationAdapter adapter,
        IEnumerable<INotificationRequestPolicy> policies, ILogger<DesktopNotificationService> logger)
    {
        _adapter = adapter; _policies = policies; _logger = logger;
        _worker = Task.Run(RunAsync);
    }

    public bool Submit(NotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_stopped) return false;
            var key = request.CoalescingKey ?? Guid.NewGuid().ToString("N");
            if (_pending.ContainsKey(key) || _pending.Count < Capacity) _pending[key] = request;
            else _overflow = request;
            if (_signal.CurrentCount == 0) _signal.Release();
            return true;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_stop.Token).ConfigureAwait(false);
                while (Take() is { } request)
                {
                    try
                    {
                        foreach (var policy in _policies)
                        {
                            request = await policy.PrepareAsync(request, _stop.Token).ConfigureAwait(false);
                            if (request is null) break;
                        }
                        if (request is null) continue;
                        _stop.Token.ThrowIfCancellationRequested();
                        using var delivery = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        delivery.CancelAfter(TimeSpan.FromSeconds(5));
                        await _adapter.ShowAsync(request, delivery.Token).WaitAsync(delivery.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                    catch (Exception error)
                    {
                        // Platform errors may embed message text; log the type, not the exception content.
                        _logger.LogWarning("Notification delivery failed ({ErrorType}).", error.GetType().Name);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private NotificationRequest? Take()
    {
        lock (_gate)
        {
            if (_stopped) return null;
            if (_pending.Count > 0)
            {
                var item = _pending.First(); _pending.Remove(item.Key); return item.Value;
            }
            var overflow = _overflow; _overflow = null; return overflow;
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopping is not null) return _stopping;
            _stopped = true; _pending.Clear(); _overflow = null; _stop.Cancel();
            return _stopping = _worker;
        }
    }

    private int _disposed;
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref _disposed, 1) == 0) { _stop.Dispose(); _signal.Dispose(); }
    }
}
