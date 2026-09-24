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

    private async Task DispatchAsync()
    {
        await foreach (var callback in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            callback(); // MeshCoreClient isolates each subscriber's exceptions.
    }

    // Do not wait for user code: a subscriber may itself call DisposeAsync.
    public void Dispose() => _queue.Writer.TryComplete();
}
