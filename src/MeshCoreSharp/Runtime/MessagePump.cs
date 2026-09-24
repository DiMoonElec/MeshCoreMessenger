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

    public void Start()
    {
        // Read the offline queue even if no notification arrived during APP_START.
        Notify();
        _ready.TrySetResult();
    }

    public void Notify() => _wake.Writer.TryWrite(true);

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            _stop.Cancel();
            _wake.Writer.TryComplete();
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
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // RX already reports malformed packets. Other command failures surface here.
                    // Stop this drain; a later notification may start another one. Never spin-retry.
                    if (!ct.IsCancellationRequested && ex is not MeshCoreProtocolException)
                        _reportError(ex);
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
}
