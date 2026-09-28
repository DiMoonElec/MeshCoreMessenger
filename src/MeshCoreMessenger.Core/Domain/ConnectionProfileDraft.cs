namespace MeshCoreMessenger.Core.Domain;

/// <summary>Editable transport settings without persistence-owned timestamps.</summary>
public sealed record ConnectionProfileDraft
{
    public Guid? Id { get; init; }
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
}
