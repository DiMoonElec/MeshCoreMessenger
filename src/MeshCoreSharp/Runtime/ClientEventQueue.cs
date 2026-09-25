using System.Threading.Channels;

namespace MeshCoreSharp.Runtime;

/// <summary>Runs user callbacks in order, independently of the protocol receive loop.</summary>
internal sealed class ClientEventQueue : IDisposable
{
    private readonly Channel<Action> _queue = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });

    public ClientEventQueue() => _ = Task.Run(DispatchAsync);

    public void Post(Action callback) => _queue.Writer.TryWrite(callback);

    public Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(() => completion.TrySetResult()))
            throw new ObjectDisposedException(nameof(MeshCoreClient), "The client event queue no longer accepts barriers.");

        // Cancelling the caller only cancels its wait. The accepted marker remains in the
        // FIFO so it cannot disturb delivery or later barriers.
        return completion.Task.WaitAsync(cancellationToken);
    }

    private async Task DispatchAsync()
    {
        await foreach (var callback in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            callback(); // MeshCoreClient isolates each subscriber's exceptions.
    }

    // Do not wait for user code: a subscriber may itself call DisposeAsync.
    public void Dispose() => _queue.Writer.TryComplete();
}
