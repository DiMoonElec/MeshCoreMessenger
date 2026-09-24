namespace MeshCoreSharp;

public sealed record MeshCoreClientOptions
{
    public string ApplicationName { get; init; } = "MeshCoreSharp";

    // Current meshcore_py and the protocol documentation use target version 3.
    public byte ApplicationProtocolVersion { get; init; } = 3;

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
