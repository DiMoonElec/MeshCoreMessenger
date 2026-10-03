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
    DateTimeOffset? NextAttemptUtc)
{
    /// <summary>Immutable configuration owned by this connection/retry cycle, not the edited database row.</summary>
    public ConnectionProfile? UsedProfile { get; init; }
    /// <summary>Actual SelfInfo name of this Online session; null outside Online.</summary>
    public string? SenderName { get; init; }
}

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

public sealed class ConnectionAttemptPersistenceException : Exception
{
    public ConnectionAttemptPersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
