namespace MeshCoreMessenger.Desktop.Lifecycle;

public interface IDraftDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemDraftDelay(TimeProvider timeProvider) : IDraftDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, timeProvider, cancellationToken);
}
