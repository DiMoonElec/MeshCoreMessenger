namespace MeshCoreMessenger.Desktop.Lifecycle;

public interface ISearchDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemSearchDelay(TimeProvider timeProvider) : ISearchDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, timeProvider, cancellationToken);
}
