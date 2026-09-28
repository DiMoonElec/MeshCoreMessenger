using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class SessionCompletionTrackerTests
{
    [Fact]
    public async Task FailedSessionEndRemainsPendingUntilExplicitRetry()
    {
        var store = new FakeSessionStore { FailuresRemaining = 1 };
        var tracker = new SessionCompletionTracker(store);
        var sessionId = Guid.NewGuid();
        var endedUtc = new DateTimeOffset(2026, 9, 28, 12, 30, 0, TimeSpan.Zero);

        var failure = await Assert.ThrowsAsync<SessionCompletionPersistenceException>(() =>
            tracker.EndAsync(sessionId, endedUtc, "Application shutdown", CancellationToken));

        Assert.IsType<IOException>(failure.InnerException);
        Assert.True(tracker.IsPaused);
        Assert.Equal(1, tracker.PendingCount);
        Assert.Equal(1, store.EndCalls);
        await Assert.ThrowsAsync<SessionCompletionPersistenceException>(
            () => tracker.FlushAsync(CancellationToken));
        Assert.Equal(1, store.EndCalls);

        await tracker.RetryAsync(CancellationToken);
        await tracker.FlushAsync(CancellationToken);

        Assert.False(tracker.IsPaused);
        Assert.Equal(0, tracker.PendingCount);
        var completed = Assert.Single(store.Completed);
        Assert.Equal(sessionId, completed.SessionId);
        Assert.Equal(endedUtc, completed.EndedUtc);
        Assert.Equal("Application shutdown", completed.Reason);
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class FakeSessionStore : ISessionStore
    {
        private int _failuresRemaining;

        public int FailuresRemaining { set => _failuresRemaining = value; }
        public int EndCalls { get; private set; }
        public List<(Guid SessionId, DateTimeOffset EndedUtc, string Reason)> Completed { get; } = [];

        public Task<SessionRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionRecord?>(null);

        public Task CreateAsync(SessionRecord session, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task BindNodeAsync(
            Guid sessionId,
            Guid nodeId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EndAsync(
            Guid sessionId,
            DateTimeOffset endedUtc,
            string reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EndCalls++;
            if (Interlocked.Decrement(ref _failuresRemaining) >= 0)
            {
                throw new IOException("Simulated session-end failure.");
            }

            Completed.Add((sessionId, endedUtc, reason));
            return Task.CompletedTask;
        }
    }
}
