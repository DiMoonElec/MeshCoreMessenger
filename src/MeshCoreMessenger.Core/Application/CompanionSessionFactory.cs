using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;

namespace MeshCoreMessenger.Core.Application;

public sealed class CompanionSessionFactory(
    IMeshCoreClientFactory clients,
    INodeStore nodes,
    ISessionStore sessions,
    IDurableSessionCompletion sessionCompletions,
    TimeProvider timeProvider) : ICompanionSessionFactory
{
    public async Task<CompanionSession> CreateAsync(
        ConnectionProfile profile,
        long generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation), "Session generation must be positive.");
        }

        var sessionId = Guid.NewGuid();
        var startedUtc = timeProvider.GetUtcNow();
        await sessions.CreateAsync(
            new SessionRecord(sessionId, profile.Id, null, startedUtc, null, null),
            cancellationToken).ConfigureAwait(false);

        try
        {
            var client = clients.Create(profile);
            return new CompanionSession(
                sessionId,
                generation,
                profile.Id,
                client,
                nodes,
                sessions,
                sessionCompletions,
                timeProvider);
        }
        catch
        {
            await sessionCompletions.EndAsync(
                sessionId,
                timeProvider.GetUtcNow(),
                "Client creation failed",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
