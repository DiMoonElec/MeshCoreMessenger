namespace MeshCoreMessenger.Core.Domain;

public enum ConnectionSupervisorState
{
    Offline,
    Connecting,
    Identifying,
    Synchronizing,
    Online,
    RetryWaiting,
    Disconnecting,
    NeedsAttention,
}

public sealed record ConnectionSupervisorSnapshot(
    ConnectionSupervisorState State,
    long Generation,
    Guid? ProfileId,
    Guid? SessionId,
    Guid? NodeId,
    string? Reason,
    DateTimeOffset? NextAttemptUtc);

public sealed class ConnectionSupervisorStateChangedEventArgs(
    ConnectionSupervisorSnapshot previous,
    ConnectionSupervisorSnapshot current) : EventArgs
{
    public ConnectionSupervisorSnapshot Previous { get; } = previous;
    public ConnectionSupervisorSnapshot Current { get; } = current;
}

public enum ConnectionAttemptPhase
{
    Identifying,
    Synchronizing,
}

public sealed class ConnectionAttemptProgressEventArgs(
    long generation,
    ConnectionAttemptPhase phase) : EventArgs
{
    public long Generation { get; } = generation;
    public ConnectionAttemptPhase Phase { get; } = phase;
}

public sealed record ConnectionAttemptCompletion(Exception Error);

public sealed class NodeIdentityMismatchException : Exception
{
    public NodeIdentityMismatchException()
        : base("The connected Companion public key does not match the selected profile.")
    {
    }
}

public sealed class ConnectionAttemptPersistenceException : Exception
{
    public ConnectionAttemptPersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
