using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Application;

internal enum PrivateDeliveryPhase { KnownRoute, Flood, FallbackFlood }

/// <summary>A single planned TX. This is a schedule, not permission to transmit or change the node.</summary>
internal sealed record PrivateRetryStep(
    int AttemptNumber, int WireMessageOrdinal, PrivateDeliveryPhase Phase, byte WireAttempt,
    bool RequiresNewTimestamp, bool ResetRouteBeforeSend)
{
    /// <summary>The caller reserves new timestamps durably before TX; this method never reads a clock.</summary>
    public uint ResolveTimestamp(uint? previousTimestamp, uint? reservedTimestamp = null)
    {
        if (AttemptNumber > 1 && previousTimestamp is null)
            throw new ArgumentException("A subsequent transmission requires the previous wire timestamp.", nameof(previousTimestamp));
        if (RequiresNewTimestamp)
        {
            if (reservedTimestamp is not { } next || previousTimestamp is { } previous && next <= previous)
                throw new ArgumentException("A new wire message requires a reserved timestamp greater than the previous one.", nameof(reservedTimestamp));
            return next;
        }
        if (previousTimestamp is not { } existing || reservedTimestamp is not null)
            throw new ArgumentException("A delivery repeat requires the previous timestamp and no new reservation.");
        return existing;
    }
}

/// <summary>Bounded application schedule: three flood TX, or three known-route TX then two flood TX.</summary>
internal sealed class PrivateRetryPlan
{
    private PrivateRetryPlan(PrivateRetryPolicy policy, PrivateRetryStep[] steps)
    {
        Policy = policy;
        Steps = Array.AsReadOnly(steps);
    }

    public PrivateRetryPolicy Policy { get; }
    public IReadOnlyList<PrivateRetryStep> Steps { get; }

    public static PrivateRetryPlan Create(bool initialFlood, PrivateRetryPolicy? policy = null)
    {
        policy ??= PrivateRetryPolicy.Default;
        var count = initialFlood ? 3 : 5;
        var steps = new PrivateRetryStep[count];
        var wireMessageOrdinal = 0;
        byte wireAttempt = 0;
        for (var index = 0; index < count; index++)
        {
            var fallbackBoundary = !initialFlood && index == 3;
            var newTimestamp = index == 0 || fallbackBoundary || policy.RetryMode == PrivateRepeatMode.NewTimestampResetAttempt;
            if (newTimestamp) { wireMessageOrdinal++; wireAttempt = 0; }
            else wireAttempt++;
            var phase = initialFlood ? PrivateDeliveryPhase.Flood
                : index < 3 ? PrivateDeliveryPhase.KnownRoute : PrivateDeliveryPhase.FallbackFlood;
            steps[index] = new(index + 1, wireMessageOrdinal, phase, wireAttempt, newTimestamp, fallbackBoundary);
        }
        return new(policy, steps);
    }
}
