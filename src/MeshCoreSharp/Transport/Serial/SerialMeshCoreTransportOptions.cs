using MeshCoreSharp.Protocol;

namespace MeshCoreSharp.Transport.Serial;

public sealed record SerialMeshCoreTransportOptions
{
    public required string PortName { get; init; }
    public int BaudRate { get; init; } = 115200;

    /// <summary>
    /// DTR level applied by System.IO.Ports after opening. Together with <see cref="RtsEnable"/>,
    /// the defaults keep both active-low ESP32 auto-reset inputs asserted, which the standard
    /// two-transistor circuit treats as a non-reset state.
    /// </summary>
    public bool DtrEnable { get; init; } = true;

    /// <summary>
    /// RTS level applied by System.IO.Ports after opening. Override both signal options only
    /// when the target board's wiring is known; applying false/false sequentially can reset ESP32 boards.
    /// </summary>
    public bool RtsEnable { get; init; } = true;

    /// <summary>Delay after opening the port before commands may be sent.</summary>
    public TimeSpan OpenDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Polling timeout for synchronous reads, in milliseconds; not a command timeout.</summary>
    public int ReadTimeout { get; init; } = 100;

    /// <summary>Maximum time for a synchronous write, in milliseconds.</summary>
    public int WriteTimeout { get; init; } = 1000;

    public int ReceiveBufferSize { get; init; } = 2048;
    public int DecoderSafetyLimit { get; init; } = ProtocolLimits.DefaultStreamDecoderSafetyLimit;
}
