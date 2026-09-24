using MeshCoreSharp.Protocol;

namespace MeshCoreSharp.Transport.Tcp;

public sealed record TcpMeshCoreTransportOptions
{
    public required string Host { get; init; }
    public required int Port { get; init; }

    public int ReceiveBufferSize { get; init; } = 2048;
    public int DecoderSafetyLimit { get; init; } = ProtocolLimits.DefaultStreamDecoderSafetyLimit;
}
