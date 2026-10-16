namespace MeshCoreSharp.Models;

/// <summary>An accepted private text. Delivery completes independently of the command queue.</summary>
/// <param name="Delivery">Confirmation or timeout; cancelled by the send token and faulted on connection loss.</param>
public sealed record TextMessageSendResult(uint Timestamp, MessageSentInfo Accepted, Task<MessageDeliveryResult> Delivery)
{
    /// <summary>The send was accepted even when restoring temporary scope failed.</summary>
    public Exception? ScopeCleanupError { get; init; }
}

public enum MessageDeliveryStatus
{
    Confirmed,
    TimedOut,
    NotExpected,
}

public sealed record MessageDeliveryResult(MessageDeliveryStatus Status, MessageAcknowledgement? Acknowledgement);
