namespace MeshCoreSharp.Transport;

public interface IMeshCoreTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    ValueTask SendAsync(
        ReadOnlyMemory<byte> companionFrame,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        CancellationToken cancellationToken = default);
}
