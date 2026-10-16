namespace MeshCoreSharp.Transport;

public interface IMeshCoreTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends one logical frame. ArgumentException must indicate validation failed before any wire bytes were sent.</summary>
    ValueTask SendAsync(
        ReadOnlyMemory<byte> companionFrame,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        CancellationToken cancellationToken = default);
}
