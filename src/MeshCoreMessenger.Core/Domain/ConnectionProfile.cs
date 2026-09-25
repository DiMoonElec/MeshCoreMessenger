namespace MeshCoreMessenger.Core.Domain;

/// <summary>Persisted connection settings. Transport-specific fields are mutually exclusive.</summary>
public sealed record ConnectionProfile
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required ConnectionTransportKind Transport { get; init; }
    public string? TcpHost { get; init; }
    public int? TcpPort { get; init; }
    public string? SerialPortName { get; init; }
    public int? BaudRate { get; init; }
    public bool DtrEnable { get; init; } = true;
    public bool RtsEnable { get; init; } = true;
    public int OpenDelayMilliseconds { get; init; } = 2_000;
    public int CommandTimeoutMilliseconds { get; init; } = 10_000;
    public int AcknowledgementTimeoutMilliseconds { get; init; } = 30_000;
    public bool AutoConnect { get; init; }
    public bool Reconnect { get; init; } = true;
    public byte[]? ExpectedNodePublicKey { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required DateTimeOffset UpdatedUtc { get; init; }
}
