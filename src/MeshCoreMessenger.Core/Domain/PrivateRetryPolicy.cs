namespace MeshCoreMessenger.Core.Domain;

/// <summary>Wire identity of the next transmission within a private delivery phase.</summary>
public enum PrivateRepeatMode
{
    SameTimestampIncrementAttempt = 0,
    NewTimestampResetAttempt = 1,
}

/// <summary>Immutable private retry policy. Route fallback always starts a new wire message.</summary>
public sealed record PrivateRetryPolicy
{
    public static PrivateRetryPolicy Default { get; } = new();

    public PrivateRetryPolicy(PrivateRepeatMode retryMode = PrivateRepeatMode.SameTimestampIncrementAttempt)
    {
        if (!Enum.IsDefined(retryMode)) throw new ArgumentOutOfRangeException(nameof(retryMode));
        RetryMode = retryMode;
    }

    public PrivateRepeatMode RetryMode { get; }
}
