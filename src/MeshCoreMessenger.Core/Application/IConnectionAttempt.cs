using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Application;

public interface IConnectionAttempt : IAsyncDisposable
{
    long Generation { get; }
    Guid? SessionId { get; }
    Guid? NodeId { get; }
    Task<ConnectionAttemptCompletion> Completion { get; }

    event EventHandler<ConnectionAttemptProgressEventArgs>? ProgressChanged;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(string reason, CancellationToken cancellationToken = default);
}

public interface IConnectionAttemptFactory
{
    Task<IConnectionAttempt> CreateAsync(
        ConnectionProfile profile,
        long generation,
        CancellationToken cancellationToken = default);
}
