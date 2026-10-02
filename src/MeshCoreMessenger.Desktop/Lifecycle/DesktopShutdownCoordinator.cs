using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Desktop.Preferences;

namespace MeshCoreMessenger.Desktop.Lifecycle;

public interface IDesktopShutdownCoordinator
{
    bool IsCompleted { get; }
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

internal interface IDesktopUiLifetime
{
    Task StopAsync();
    Task ReportShutdownFailureAsync(Exception exception);
}

/// <summary>
/// Quiesces Desktop work and accepts application exit only after all durable writers have flushed.
/// A failed attempt remains retryable and never owns or disposes SQLite or the instance lock.
/// </summary>
internal sealed class DesktopShutdownCoordinator(
    IDesktopUiLifetime ui,
    IDesktopConnectionLifecycle connections,
    IDurableMessageIngress ingress,
    IDurableSessionCompletion sessionCompletions,
    IDurableReadStateWrites readStates,
    IDurableDraftWrites drafts,
    IDurableDesktopPreferences preferences,
    ILogger<DesktopShutdownCoordinator> logger) : IDesktopShutdownCoordinator
{
    private readonly object _gate = new();
    private Task? _attempt;
    private bool _uiStopped;
    private bool _connectionsStopped;
    private bool _completed;
    private int _attemptNumber;

    public bool IsCompleted
    {
        get
        {
            lock (_gate)
            {
                return _completed;
            }
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await ShutdownAsync(reportFailure: true, cancellationToken).ConfigureAwait(false);
    }

    internal Task ShutdownForProcessExitAsync(CancellationToken cancellationToken = default) =>
        ShutdownAsync(reportFailure: false, cancellationToken);

    private async Task ShutdownAsync(bool reportFailure, CancellationToken cancellationToken)
    {
        Task attempt;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            if (_attempt is null)
            {
                var created = ShutdownAttemptAsync(++_attemptNumber, reportFailure);
                _attempt = created;
                attempt = created;
                _ = created.ContinueWith(
                    completed =>
                    {
                        if (completed.IsCompletedSuccessfully)
                        {
                            return;
                        }

                        lock (_gate)
                        {
                            if (ReferenceEquals(_attempt, completed) && !_completed)
                            {
                                _attempt = null;
                            }
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else
            {
                attempt = _attempt;
            }
        }

        await attempt.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ShutdownAttemptAsync(int attemptNumber, bool reportFailure)
    {
        try
        {
            if (!_uiStopped)
            {
                await ui.StopAsync().ConfigureAwait(false);
                _uiStopped = true;
            }

            if (!_connectionsStopped)
            {
                await connections.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
                _connectionsStopped = true;
            }

            if (attemptNumber > 1)
            {
                if (ingress.IsPaused)
                {
                    await ingress.RetryAsync(CancellationToken.None).ConfigureAwait(false);
                }

                if (sessionCompletions.IsPaused)
                {
                    await sessionCompletions.RetryAsync(CancellationToken.None).ConfigureAwait(false);
                }

                if (readStates.IsPaused)
                {
                    await readStates.RetryAsync(CancellationToken.None).ConfigureAwait(false);
                }

                if (drafts.IsPaused)
                {
                    await drafts.RetryAsync(CancellationToken.None).ConfigureAwait(false);
                }

                if (preferences.IsPaused)
                {
                    await preferences.RetryAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            await ingress.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await sessionCompletions.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await readStates.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await drafts.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await preferences.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                _completed = true;
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Desktop shutdown did not reach a durable commit barrier.");
            if (reportFailure)
            {
                try
                {
                    await ui.ReportShutdownFailureAsync(exception).ConfigureAwait(false);
                }
                catch (Exception reportingException)
                {
                    logger.LogWarning(reportingException, "Could not report the Desktop shutdown failure.");
                }
            }

            throw new DesktopShutdownException(
                "Application shutdown was canceled because local persistence did not complete.",
                exception);
        }
    }
}

internal sealed class DesktopShutdownException(string message, Exception innerException)
    : Exception(message, innerException);
