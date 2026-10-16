namespace MeshCoreSharp.Models;

/// <summary>A channel message accepted by the Companion. Channels have no delivery ACK.</summary>
public sealed record ChannelMessageSendResult(byte ChannelIndex, uint Timestamp)
{
    /// <summary>The send was accepted even when restoring temporary scope failed. Do not resend automatically.</summary>
    public Exception? ScopeCleanupError { get; init; }
}
