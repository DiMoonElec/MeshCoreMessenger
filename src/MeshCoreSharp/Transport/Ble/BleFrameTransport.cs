using MeshCoreSharp.Exceptions;

namespace MeshCoreSharp.Transport.Ble;

// Each attempt owns its cancellation, receiver, and cleanup. No completion mutates a later attempt.
internal sealed class BleFrameTransport(BleMeshCoreTransportOptions options, IBleGattOpenPlatform platform,
    TimeProvider time) : IMeshCoreTransport
{
    private sealed class Generation
    {
        internal readonly CancellationTokenSource StopSource = new();
        internal readonly TaskCompletionSource Opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource OpenFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cleaned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal BleReceiveSession? Receiver;
        internal Task OpenCleanup = Task.CompletedTask;
        internal Task Cancellation = Task.CompletedTask;
        internal bool Stopped;
    }

    private readonly object _sync = new();
    private readonly List<Task> _cleanupTasks = [];
    private Generation? _current;
    private bool _disposed;
    internal Task CleanupCompletion { get { lock (_sync) return Task.WhenAll(_cleanupTasks); } }
    public bool IsConnected { get { lock (_sync) return _current is { Stopped: false, Receiver.IsConnected: true }; } }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        Generation generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current is { Stopped: false } current)
            {
                if (current.Receiver?.Termination.IsCompleted == true) Retire(current);
                else if (current.Receiver?.IsConnected == true) return Task.CompletedTask;
                else throw new InvalidOperationException("A BLE connection attempt is already in progress.");
            }
            generation = new();
            _current = generation;
            _cleanupTasks.RemoveAll(task => task.IsCompletedSuccessfully);
            _cleanupTasks.Add(generation.Cleaned.Task);
            BleReceiveSession.Observe(generation.Cleaned.Task);
        }
        _ = OpenAsync(generation, cancellationToken);
        return generation.Opened.Task;
    }

    private async Task OpenAsync(Generation generation, CancellationToken cancellationToken)
    {
        var opener = new BleGattSessionOpener(platform, time);
        using var deadline = new CancellationTokenSource(options.OpenTimeout, time);
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, generation.StopSource.Token);
        BleGattOpenResult? prepared = null;
        try
        {
            prepared = await opener.OpenAsync(options, opening.Token).ConfigureAwait(false);
            var receiver = new BleReceiveSession(prepared, options, time);
            lock (_sync)
            {
                generation.Receiver = receiver;
                if (generation.Stopped) receiver.Stop();
            }
            _ = RetireOnTerminationAsync(generation, receiver);
            opening.Token.ThrowIfCancellationRequested();
            await receiver.StartAsync(opening.Token).ConfigureAwait(false);
            lock (_sync)
            {
                opening.Token.ThrowIfCancellationRequested();
                if (generation.Stopped || !receiver.IsConnected)
                    throw new MeshCoreTransportException("The BLE generation closed while opening.");
                generation.Opened.TrySetResult();
            }
        }
        catch (Exception error)
        {
            var failure = error is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? new OperationCanceledException(cancellationToken)
                : error is OperationCanceledException && deadline.IsCancellationRequested
                    ? new MeshCoreBleTransportException(MeshCoreBleFailureReason.DeviceUnreachable,
                        "BLE opening and notification subscription exceeded the open deadline.", error)
                    : error;
            generation.OpenCleanup = generation.Receiver is null && prepared is not null
                ? prepared.Attempt.CloseAsync(failure) : opener.CleanupCompletion;
            Retire(generation, failure);
            if (failure is OperationCanceledException cancelled)
                generation.Opened.TrySetCanceled(cancelled.CancellationToken);
            else generation.Opened.TrySetException(failure);
        }
        finally { generation.OpenFinished.TrySetResult(); }
    }

    private async Task RetireOnTerminationAsync(Generation generation, BleReceiveSession receiver)
    {
        await receiver.Termination.ConfigureAwait(false);
        Retire(generation, receiver.Error);
    }

    private void Retire(Generation generation, Exception? error = null)
    {
        lock (_sync)
        {
            if (generation.Stopped) return;
            generation.Stopped = true;
            generation.Receiver?.Stop(error);
            generation.Cancellation = generation.StopSource.CancelAsync();
            _ = Task.Run(() => CleanupAsync(generation));
        }
    }

    private static async Task CleanupAsync(Generation generation)
    {
        var errors = new List<Exception>();
        await generation.OpenFinished.Task.ConfigureAwait(false);
        try { await generation.Cancellation.ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        try
        {
            if (generation.Receiver is { } receiver)
                await receiver.CleanupCompletion.ConfigureAwait(false);
            await generation.OpenCleanup.ConfigureAwait(false);
        }
        catch (Exception error) { errors.Add(error); }
        generation.StopSource.Dispose();
        if (errors.Count == 0) generation.Cleaned.TrySetResult();
        else generation.Cleaned.TrySetException(new AggregateException("BLE generation cleanup failed.", errors));
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> companionFrame, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_current is not { Stopped: false, Receiver: { } receiver })
                throw new MeshCoreTransportException("BLE is not connected.");
            return receiver.SendAsync(companionFrame, cancellationToken);
        }
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
            return (_current?.Receiver ?? throw new MeshCoreTransportException("BLE is not connected."))
                .ReceiveAsync(cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task cleanup;
        lock (_sync)
        {
            if (_current is null) return;
            Retire(_current);
            cleanup = _current.Cleaned.Task;
        }
        try { await cleanup.WaitAsync(options.ShutdownTimeout, time, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { /* Late ownership and diagnostics remain available through CleanupCompletion. */ }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync) _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
    }
}
