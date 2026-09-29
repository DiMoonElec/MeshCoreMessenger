using System.Collections.Concurrent;
using System.Diagnostics;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class MessageIngestorTests
{
    [Fact]
    public async Task FlushWaitsForDurableStoreThenPublishesCommitInSequence()
    {
        var store = new ControlledStore { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var ingestor = new MessageIngestor(store, TimeProvider.System);
        var committed = new List<StoredIncomingMessage>();
        ingestor.MessageCommitted += (_, change) =>
        {
            Assert.True(store.IsPersisted(change.Message.EventId));
            committed.Add(change.Message);
        };

        var first = ingestor.Enqueue(Guid.NewGuid(), Guid.NewGuid(), Channel("one"));
        var second = ingestor.Enqueue(Guid.NewGuid(), Guid.NewGuid(), Channel("two"));
        var flush = ingestor.FlushAsync(CancellationToken);
        await Task.Yield();
        Assert.False(flush.IsCompleted);
        Assert.Equal(2, ingestor.PendingMessageCount);

        store.Gate.SetResult();
        await flush;
        Assert.Equal(new[] { first, second }, committed.Select(item => item.EventId));
        Assert.Equal(0, ingestor.PendingMessageCount);
        Assert.Equal(0, ingestor.PendingByteCount);
    }

    [Fact]
    public async Task CallbackCopiesBytesAndDoesNotWaitForStorage()
    {
        var store = new ControlledStore { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var ingestor = new MessageIngestor(store, TimeProvider.System);
        var payload = new byte[] { 1, 2, 3 };
        var message = new ChannelDataMessage(1, 2, 42, payload, null);

        var started = Stopwatch.GetTimestamp();
        ingestor.Enqueue(Guid.NewGuid(), Guid.NewGuid(), message);
        var elapsed = Stopwatch.GetElapsedTime(started);
        payload[0] = 99;
        Assert.True(elapsed < TimeSpan.FromSeconds(1));
        store.Gate.SetResult();
        await ingestor.FlushAsync(CancellationToken);
        var stored = Assert.IsType<ChannelDataMessage>(Assert.Single(store.Stored).Message);
        Assert.Equal(new byte[] { 1, 2, 3 }, stored.Data.ToArray());
    }

    [Fact]
    public async Task FailurePausesWithoutDroppingWorkAndRetryResumesIt()
    {
        var store = new ControlledStore { FailuresRemaining = 1 };
        await using var ingestor = new MessageIngestor(store, TimeProvider.System);
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        ingestor.Failed += (_, error) => failed.TrySetResult(error.Exception);
        var eventId = ingestor.Enqueue(Guid.NewGuid(), Guid.NewGuid(), Channel("retry"));

        await failed.Task.WaitAsync(CancellationToken);
        Assert.True(ingestor.IsPaused);
        Assert.Equal(1, ingestor.PendingMessageCount);
        var flushError = await Assert.ThrowsAsync<ReceiveIngestException>(
            () => ingestor.FlushAsync(CancellationToken));
        Assert.IsType<IOException>(flushError.InnerException);
        await ingestor.RetryAsync(CancellationToken);
        await ingestor.FlushAsync(CancellationToken);
        Assert.False(ingestor.IsPaused);
        Assert.Equal(eventId, Assert.Single(store.Stored).EventId);
    }

    [Fact]
    public async Task CancelledFlushDoesNotDamageLaterBarrier()
    {
        var store = new ControlledStore { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var ingestor = new MessageIngestor(store, TimeProvider.System);
        ingestor.Enqueue(Guid.NewGuid(), Guid.NewGuid(), Channel("held"));
        using var cancellation = new CancellationTokenSource();
        var flush = ingestor.FlushAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush);

        store.Gate.SetResult();
        await ingestor.FlushAsync(CancellationToken);
        Assert.Single(store.Stored);
    }

    [Fact]
    public async Task FailureRaisedDuringFlushCompletesItWithErrorAndKeepsWorkForRetry()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ControlledStore
        {
            Gate = gate,
            StoreStarted = started,
            FailuresRemaining = 1,
        };
        await using var ingestor = new MessageIngestor(store, TimeProvider.System);
        ingestor.Enqueue(Guid.NewGuid(), Guid.NewGuid(), Channel("shutdown boundary"));
        await started.Task.WaitAsync(CancellationToken);

        var flush = ingestor.FlushAsync(CancellationToken);
        Assert.False(flush.IsCompleted);
        gate.SetResult();

        await Assert.ThrowsAsync<ReceiveIngestException>(() => flush);
        Assert.True(ingestor.IsPaused);
        Assert.Equal(1, ingestor.PendingMessageCount);

        await ingestor.RetryAsync(CancellationToken);
        await ingestor.FlushAsync(CancellationToken);
        Assert.Single(store.Stored);
    }

    private static ChannelMessage Channel(string text) => new(1, 1, MessageTextType.Plain, DateTimeOffset.UtcNow, text, null);
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class ControlledStore : IIncomingMessageStore
    {
        private readonly ConcurrentDictionary<Guid, byte> _persisted = new();
        private int _failuresRemaining;
        public TaskCompletionSource? Gate { get; init; }
        public TaskCompletionSource? StoreStarted { get; init; }
        public int FailuresRemaining { set => _failuresRemaining = value; }
        public ConcurrentQueue<IncomingMessageEnvelope> Stored { get; } = [];

        public async Task<StoredIncomingMessage> StoreAsync(IncomingMessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            StoreStarted?.TrySetResult();
            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(cancellationToken);
            }
            if (Interlocked.Decrement(ref _failuresRemaining) >= 0)
            {
                throw new IOException("Simulated durable-store failure.");
            }
            Stored.Enqueue(envelope);
            _persisted.TryAdd(envelope.EventId, 0);
            return new StoredIncomingMessage(
                Guid.NewGuid(),
                envelope.EventId,
                envelope.NodeId,
                Guid.NewGuid(),
                Stored.Count,
                true);
        }
        public bool IsPersisted(Guid eventId) => _persisted.ContainsKey(eventId);
    }
}
