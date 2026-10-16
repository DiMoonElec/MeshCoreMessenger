using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Runtime;

/// <summary>All future login/status/path/binary APIs must share this session owner.
/// Gate order: MeshRequestGate -> ScopeOperationGate -> CommandGate.
/// Only MeshRequestGate spans the remote radio wait.</summary>
internal sealed class MeshRequestCoordinator(PacketRouter router, CancellationToken sessionToken)
{
    private readonly SemaphoreSlim _meshRequestGate = new(1, 1);

    internal async Task<BinaryResponsePacket> ExecuteBinaryAsync(
        Func<Action<MessageSentInfo>, CancellationToken, Task> send,
        TimeSpan? responseTimeout, CancellationToken token)
    {
        if (responseTimeout is { } timeout && (timeout < TimeSpan.FromMilliseconds(1) || timeout > TimeSpan.FromMinutes(1)))
            throw new ArgumentOutOfRangeException(nameof(responseTimeout));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, sessionToken);
        await _meshRequestGate.WaitAsync(linked.Token).ConfigureAwait(false);
        using var waiter = new BinaryResponseWaiter(responseTimeout);
        using var registration = router.Observe(waiter);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            await send(waiter.Bind, linked.Token).ConfigureAwait(false);
            return await waiter.Completion.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        finally { _meshRequestGate.Release(); }
    }
}

internal sealed class BinaryResponseWaiter(TimeSpan? responseTimeout, TimeProvider? clock = null) : IRuntimePacketObserver, IDisposable
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource<BinaryResponsePacket> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint? _tag;
    private ITimer? _timer;
    private bool _stopped;
    internal Task<BinaryResponsePacket> Completion => _completion.Task;

    internal void Bind(MessageSentInfo acceptance)
    {
        lock (_sync)
        {
            if (_stopped || _tag is not null) throw new MeshCoreProtocolException("Remote acceptance belongs to a closed or already bound request.");
            _tag = acceptance.ExpectedAck;
            var timeout = responseTimeout ?? TimeSpan.FromMilliseconds(Math.Clamp((long)acceptance.SuggestedTimeoutMilliseconds + 1000, 1000, 60000));
            // Deadline begins on RX acceptance, not when the native write eventually returns.
            _timer = (clock ?? TimeProvider.System).CreateTimer(_ => Fail(new MeshCoreTimeoutException("RemoteBinaryResponse", timeout)),
                null, timeout, Timeout.InfiniteTimeSpan);
        }
    }
    public bool Observe(CompanionPacket packet)
    {
        if (packet is not BinaryResponsePacket response) return false;
        lock (_sync)
        {
            if (_stopped || _tag is null || response.Tag != _tag) return false;
            _stopped = true;
            return _completion.TrySetResult(response);
        }
    }
    public void Fail(Exception error)
    {
        lock (_sync)
        {
            if (_stopped) return;
            _stopped = true; _completion.TrySetException(error);
        }
    }
    public void Dispose()
    {
        lock (_sync) { _stopped = true; _timer?.Dispose(); }
        if (_completion.Task.IsFaulted) _ = _completion.Task.Exception;
    }
}
