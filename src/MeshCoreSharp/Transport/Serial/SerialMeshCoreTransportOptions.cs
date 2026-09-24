using MeshCoreSharp.Protocol;

namespace MeshCoreSharp.Transport.Serial;

public sealed record SerialMeshCoreTransportOptions
{
    public required string PortName { get; init; }
    public int BaudRate { get; init; } = 115200;
    public bool DtrEnable { get; init; } = true;
    public bool RtsEnable { get; init; }

    /// <summary>Delay after opening the port before commands may be sent.</summary>
    public TimeSpan OpenDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Polling timeout for synchronous reads, in milliseconds; not a command timeout.</summary>
    public int ReadTimeout { get; init; } = 100;

    /// <summary>Maximum time for a synchronous write, in milliseconds.</summary>
    public int WriteTimeout { get; init; } = 1000;

    public int ReceiveBufferSize { get; init; } = 2048;
    public int DecoderSafetyLimit { get; init; } = ProtocolLimits.DefaultStreamDecoderSafetyLimit;
}
