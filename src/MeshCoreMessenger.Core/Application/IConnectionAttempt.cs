using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Application;

public interface IConnectionAttempt : IAsyncDisposable
{
    long Generation { get; }
    Guid? SessionId { get; }
    Guid? NodeId { get; }
    /// <summary>Read-only SelfInfo metadata, never a cached display label.</summary>
    string? LocalNodeName => null;
    Task<ConnectionAttemptCompletion> Completion { get; }

    event EventHandler<ConnectionAttemptProgressEventArgs>? ProgressChanged;

    void OpenCommandAdmission() { }
    void CloseCommandAdmission() { }

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
