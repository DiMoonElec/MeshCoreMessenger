using System.Threading.Channels;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Runtime;

internal sealed class MessagePump : IAsyncDisposable
{
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false,
    });
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop;
    private readonly Func<CancellationToken, Task<MessageQueuePacket>> _receiveNext;
    private readonly Action<Exception> _reportError;
    private readonly Task _worker;
    private readonly object _sync = new();
    private readonly List<TaskCompletionSource> _drainWaiters = [];
    private Exception? _terminalError;
    private bool _draining;
    private int _stopped;

    public MessagePump(
        Func<CancellationToken, Task<MessageQueuePacket>> receiveNext,
        Action<Exception> reportError,
        CancellationToken connectionToken)
    {
        _receiveNext = receiveNext;
        _reportError = reportError;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        _worker = Task.Run(RunAsync);
    }

    public void Start(bool drainOfflineMessages)
    {
        // Read the offline queue even if no notification arrived during APP_START.
        // Queue the wake before releasing the worker so a pre-start notification
        // and the initial drain cannot race into two separate empty passes.
        if (drainOfflineMessages)
            Notify();
        _ready.TrySetResult();
    }

    public void Notify()
    {
        if (Volatile.Read(ref _stopped) == 0)
            _wake.Writer.TryWrite(true);
    }

    public Task DrainAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource completion;
        var wake = false;
        lock (_sync)
        {
            if (_terminalError is not null)
                return Task.FromException(_terminalError);
            if (Volatile.Read(ref _stopped) != 0)
                return Task.FromCanceled(new CancellationToken(canceled: true));

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _drainWaiters.Add(completion);
            wake = !_draining;
        }

        if (wake)
            Notify();
        return WaitForDrainAsync(completion, cancellationToken);
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            _stop.Cancel();
            _wake.Writer.TryComplete();
            CompleteWaiters(canceled: true);
        }
    }

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        try
        {
            await _ready.Task.WaitAsync(ct).ConfigureAwait(false);
            while (await _wake.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_wake.Reader.TryRead(out _)) { }
                lock (_sync)
                {
                    if (_terminalError is not null)
                        continue;
                    _draining = true;
                }

                try
                {
                    while (true)
                    {
                        // Notifications seen before this query are covered by it.
                        // A tickle during the final query remains pending, avoiding a lost wake-up.
                        while (_wake.Reader.TryRead(out _)) { }
                        ct.ThrowIfCancellationRequested();
                        var packet = await _receiveNext(ct).ConfigureAwait(false);
                        if (packet is NoMoreMessagesPacket)
                            break;
                    }
                    CompleteWaiters();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // RX already reports malformed packets. Other command failures surface here.
                    // Stop this drain; a later notification may resume a recoverable failure.
                    // A timeout is terminal for this connection because late replies have no request ID.
                    if (!ct.IsCancellationRequested && ex is not MeshCoreProtocolException)
                        _reportError(ex);
                    FailWaiters(ex, terminal: ex is MeshCoreTimeoutException);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await _worker.ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task WaitForDrainAsync(TaskCompletionSource completion, CancellationToken cancellationToken)
    {
        try
        {
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!completion.Task.IsCompleted)
            {
                lock (_sync)
                    _drainWaiters.Remove(completion);
            }
        }
    }

    private void CompleteWaiters(bool canceled = false)
    {
        TaskCompletionSource[] waiters;
        lock (_sync)
        {
            _draining = false;
            waiters = [.. _drainWaiters];
            _drainWaiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            if (canceled)
                waiter.TrySetCanceled(_stop.Token);
            else
                waiter.TrySetResult();
        }
    }

    private void FailWaiters(Exception exception, bool terminal)
    {
        TaskCompletionSource[] waiters;
        lock (_sync)
        {
            _draining = false;
            waiters = [.. _drainWaiters];
            _drainWaiters.Clear();
            if (terminal)
            {
                _terminalError = new MeshCoreProtocolException(
                    "Incoming message draining cannot safely resume after a timed-out SYNC_NEXT_MESSAGE response. Reconnect before draining again.",
                    exception);
            }
        }

        foreach (var waiter in waiters)
            waiter.TrySetException(exception);
    }
}
