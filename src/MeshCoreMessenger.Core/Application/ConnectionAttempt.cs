using System.Runtime.ExceptionServices;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp;
using MeshCoreSharp.Exceptions;

namespace MeshCoreMessenger.Core.Application;

public sealed class ConnectionAttemptFactory(
    ICompanionSessionFactory sessions,
    DirectoryService directories,
    IDirectoryStore directoryStore,
    MessageIngestor ingestor) : IConnectionAttemptFactory
{
    public async Task<IConnectionAttempt> CreateAsync(
        ConnectionProfile profile,
        long generation,
        CancellationToken cancellationToken = default)
    {
        var session = await sessions.CreateAsync(profile, generation, cancellationToken).ConfigureAwait(false);
        try
        {
            var coordinator = new ReceiveCoordinator(directories, directoryStore, ingestor);
            return new ConnectionAttempt(session, coordinator);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal sealed class ConnectionAttempt : IConnectionAttempt
{
    private readonly CompanionSession _session;
    private readonly ReceiveCoordinator _coordinator;
    private readonly TaskCompletionSource<ConnectionAttemptCompletion> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private readonly CancellationTokenSource _observeStop = new();
    private int _stopped;
    private int _disposed;

    public ConnectionAttempt(CompanionSession session, ReceiveCoordinator coordinator)
    {
        _session = session;
        _coordinator = coordinator;
        _session.LifecycleChanged += OnLifecycleChanged;
        _ = ObserveCoordinatorFailureAsync();
    }

    public long Generation => _session.Generation;
    public Guid? SessionId => _session.SessionId;
    public Guid? NodeId => _session.LocalNodeId;
    public Task<ConnectionAttemptCompletion> Completion => _completion.Task;

    public event EventHandler<ConnectionAttemptProgressEventArgs>? ProgressChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _session.StartAsync(cancellationToken).ConfigureAwait(false);
        RaiseProgress(ConnectionAttemptPhase.Synchronizing);
        await _coordinator.SynchronizeAsync(_session, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await _stopGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }

            Exception? cleanupError = null;
            try
            {
                await _coordinator.QuiesceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupError = exception;
            }

            try
            {
                await _session.StopAsync(reason.Trim(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupError ??= exception;
            }

            try
            {
                await _coordinator.CompleteAfterSessionStopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // A persistence failure must dominate transport cleanup errors: reconnecting
                // while accepted messages are still uncommitted would violate the receive barrier.
                cleanupError = new ConnectionAttemptPersistenceException(
                    "Could not commit all incoming messages while closing the connection attempt.",
                    exception);
            }

            _session.LifecycleChanged -= OnLifecycleChanged;
            if (cleanupError is not null)
            {
                ExceptionDispatchInfo.Capture(cleanupError).Throw();
            }
        }
        finally
        {
            _stopGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await StopAsync("Disposed", CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _observeStop.Cancel();
            _session.LifecycleChanged -= OnLifecycleChanged;
            await _coordinator.DisposeAsync().ConfigureAwait(false);
            await _session.DisposeAsync().ConfigureAwait(false);
            _observeStop.Dispose();
            _stopGate.Dispose();
        }
    }

    private async Task ObserveCoordinatorFailureAsync()
    {
        try
        {
            var exception = await _coordinator.Failure.WaitAsync(_observeStop.Token).ConfigureAwait(false);
            _completion.TrySetResult(new ConnectionAttemptCompletion(exception));
        }
        catch (OperationCanceledException) when (_observeStop.IsCancellationRequested)
        {
        }
    }

    private void OnLifecycleChanged(object? sender, CompanionSessionLifecycleEventArgs args)
    {
        if (args.Generation != Generation || args.SessionId != SessionId)
        {
            return;
        }

        if (args.SessionState == CompanionSessionState.Identifying)
        {
            RaiseProgress(ConnectionAttemptPhase.Identifying);
        }

        if (args.ConnectionState == MeshCoreConnectionState.Faulted)
        {
            _completion.TrySetResult(new ConnectionAttemptCompletion(
                new MeshCoreTransportException("The Companion connection was lost.")));
        }
    }

    private void RaiseProgress(ConnectionAttemptPhase phase)
    {
        var args = new ConnectionAttemptProgressEventArgs(Generation, phase);
        foreach (EventHandler<ConnectionAttemptProgressEventArgs> handler in
                 ProgressChanged?.GetInvocationList().Cast<EventHandler<ConnectionAttemptProgressEventArgs>>() ?? [])
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // Progress observers must not control the session lifecycle.
            }
        }
    }
}
