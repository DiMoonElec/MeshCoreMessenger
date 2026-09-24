namespace MeshCoreSharp;

public sealed record MeshCoreClientOptions
{
    public string ApplicationName { get; init; } = "MeshCoreSharp";

    // Current meshcore_py and the protocol documentation use target version 3.
    public byte ApplicationProtocolVersion { get; init; } = 3;

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Drain incoming messages after APP_START and on MESSAGES_WAITING notifications.</summary>
    public bool AutoReceiveMessages { get; init; } = true;

    /// <summary>Added to the firmware's suggested delivery timeout.</summary>
    public TimeSpan AckTimeoutMargin { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MinimumAckTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumAckTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Maximum silence between accepted contact stream frames.</summary>
    public TimeSpan ContactsInactivityTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum duration of the entire contacts exchange, excluding queue time.</summary>
    public TimeSpan ContactsAbsoluteTimeout { get; init; } = TimeSpan.FromMinutes(2);
}
