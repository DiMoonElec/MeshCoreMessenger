namespace MeshCoreMessenger.Core.Application;

public interface IReconnectDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public interface IReconnectJitter
{
    double GetJitterFraction(int retryNumber);
}

public sealed class SystemReconnectDelay : IReconnectDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

public sealed class RandomReconnectJitter : IReconnectJitter
{
    public double GetJitterFraction(int retryNumber) => (Random.Shared.NextDouble() * 0.4) - 0.2;
}

internal static class ConnectionRetryPolicy
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
    ];

    public static TimeSpan GetDelay(int retryNumber, IReconnectJitter jitter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retryNumber);
        ArgumentNullException.ThrowIfNull(jitter);
        var fraction = jitter.GetJitterFraction(retryNumber);
        if (fraction is < -0.2 or > 0.2 || double.IsNaN(fraction))
        {
            throw new InvalidOperationException("Reconnect jitter must be between -0.2 and 0.2.");
        }

        var baseline = Delays[Math.Min(retryNumber, Delays.Length - 1)];
        return TimeSpan.FromTicks((long)Math.Round(baseline.Ticks * (1 + fraction)));
    }
}
