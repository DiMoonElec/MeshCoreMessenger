using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class ConversationReadStateTrackerTests
{
    private static readonly Guid NodeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ConversationId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task ConcurrentTargetsAreSerializedAndCoalescedWithoutLosingTheNewest()
    {
        var store = new FakeStore
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var tracker = new ConversationReadStateTracker(store);
        var first = tracker.AdvanceAsync(Position(10), CancellationToken);
        await store.Started.Task.WaitAsync(CancellationToken);
        var second = tracker.AdvanceAsync(Position(20), CancellationToken);

        store.Gate.SetResult();
        var states = await Task.WhenAll(first, second);

        Assert.Equal([10, 20], store.Writes.Select(item => item.LocalSequence));
        Assert.All(states, state => Assert.Equal(20, state.LastReadSequence));
        Assert.Equal(0, tracker.PendingCount);
        Assert.False(tracker.IsPaused);
    }

    [Fact]
    public async Task FailureKeepsPendingTargetAndRetryProducesNoFalseSuccess()
    {
        var store = new FakeStore();
        store.Failures.Enqueue(new IOException("disk full"));
        var tracker = new ConversationReadStateTracker(store);

        await Assert.ThrowsAsync<ReadStatePersistenceException>(
            () => tracker.AdvanceAsync(Position(10), CancellationToken));

        Assert.True(tracker.IsPaused);
        Assert.Equal(1, tracker.PendingCount);
        await Assert.ThrowsAsync<ReadStatePersistenceException>(
            () => tracker.FlushAsync(CancellationToken));

        await tracker.RetryAsync(CancellationToken);
        await tracker.FlushAsync(CancellationToken);

        Assert.False(tracker.IsPaused);
        Assert.Equal(0, tracker.PendingCount);
        Assert.Equal([10, 10], store.Writes.Select(item => item.LocalSequence));
    }

    private static HistoryMessagePosition Position(long sequence) =>
        new(NodeId, ConversationId, GuidFromSequence(sequence), sequence);

    private static Guid GuidFromSequence(long sequence)
    {
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes, sequence);
        return new Guid(bytes);
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class FakeStore : IConversationReadStateStore
    {
        public Queue<Exception> Failures { get; } = [];
        public List<HistoryMessagePosition> Writes { get; } = [];
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Gate { get; init; }

        public Task<ConversationReadState> GetAsync(
            Guid nodeId,
            Guid conversationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<ConversationReadState> AdvanceAsync(
            HistoryMessagePosition through,
            CancellationToken cancellationToken = default)
        {
            Writes.Add(through);
            Started.TrySetResult();
            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(cancellationToken);
            }

            if (Failures.TryDequeue(out var failure))
            {
                throw failure;
            }

            return new ConversationReadState(
                through.NodeId,
                through.ConversationId,
                through.LocalSequence,
                0,
                null);
        }
    }
}
