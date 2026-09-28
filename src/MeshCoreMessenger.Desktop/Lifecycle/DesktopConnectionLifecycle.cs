using MeshCoreMessenger.Core.Application;

namespace MeshCoreMessenger.Desktop.Lifecycle;

internal interface IDesktopConnectionLifecycle
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

/// <summary>Owns the single Desktop startup and shutdown invocation of the connection supervisor.</summary>
internal sealed class DesktopConnectionLifecycle(IConnectionSupervisor supervisor) :
    IDesktopConnectionLifecycle,
    IAsyncDisposable
{
    private readonly object _gate = new();
    private Task? _startup;
    private Task? _shutdown;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Task startup;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_shutdown is not null, this);
            _startup ??= supervisor.StartAutoConnectAsync(CancellationToken.None);
            startup = _startup;
        }

        return startup.WaitAsync(cancellationToken);
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        Task shutdown;
        lock (_gate)
        {
            _shutdown ??= ShutdownCoreAsync(_startup);
            shutdown = _shutdown;
        }

        return shutdown.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ShutdownCoreAsync(Task? startup)
    {
        if (startup is not null)
        {
            try
            {
                await startup.ConfigureAwait(false);
            }
            catch
            {
                // Startup reports its own failure. Shutdown must still stop the supervisor loop.
            }
        }

        await supervisor.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
