using System.Threading.Channels;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Application;

public interface IDurableMessageIngress
{
    bool IsPaused { get; }
    int PendingMessageCount { get; }
    Task FlushAsync(CancellationToken cancellationToken = default);
    Task RetryAsync(CancellationToken cancellationToken = default);
}

/// <summary>Single-consumer durable ingress. Event callbacks enqueue copied DTOs and never await SQLite.</summary>
public sealed class MessageIngestor : IDurableMessageIngress, IAsyncDisposable
{
    private const int OverloadMessageCount = 1_000;
    private const long OverloadByteCount = 16L * 1024 * 1024;
    private readonly IIncomingMessageStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly SemaphoreSlim _resume = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _failureGate = new();
    private readonly Task _consumer;
    private Exception? _failure;
    private TaskCompletionSource<Exception> _failureSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _queued;
    private long _queuedBytes;
    private int _disposed;

    public MessageIngestor(IIncomingMessageStore store, TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
        _consumer = Task.Run(ConsumeAsync);
    }

    public event EventHandler<IncomingMessageCommitEvent>? MessageCommitted;
    public event EventHandler<MessageIngestorErrorEventArgs>? Failed;
    public bool IsPaused => Volatile.Read(ref _failure) is not null;
    public bool IsOverloaded => Volatile.Read(ref _queued) >= OverloadMessageCount ||
        Volatile.Read(ref _queuedBytes) >= OverloadByteCount;
    public int PendingMessageCount => Math.Max(0, Volatile.Read(ref _queued));
    public long PendingByteCount => Math.Max(0, Volatile.Read(ref _queuedBytes));

    public Guid Enqueue(Guid sessionId, Guid nodeId, ReceivedMessage message, ChannelBindingRecord? stableBinding = null, byte[]? unknownChannelIdentity = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(message);
        var envelope = new IncomingMessageEnvelope(Guid.NewGuid(), sessionId, nodeId, Copy(message), _timeProvider.GetUtcNow(), stableBinding, unknownChannelIdentity?.ToArray());
        Queue(envelope);
        return envelope.EventId;
    }

    public Task EnqueueAsync(IncomingMessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(envelope);
        var copy = Copy(envelope);
        var size = EstimateSize(copy);
        Interlocked.Increment(ref _queued);
        Interlocked.Add(ref _queuedBytes, size);
        return WriteAsync(copy, size, cancellationToken);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Task<Exception> failure;
        lock (_failureGate)
        {
            if (_failure is { } currentFailure)
            {
                throw new ReceiveIngestException(currentFailure);
            }

            failure = _failureSignal.Task;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new BarrierWork(completion), cancellationToken).ConfigureAwait(false);
        var barrier = completion.Task.WaitAsync(cancellationToken);
        var completed = await Task.WhenAny(barrier, failure).ConfigureAwait(false);
        if (completed == failure)
        {
            throw new ReceiveIngestException(await failure.ConfigureAwait(false));
        }

        await barrier.ConfigureAwait(false);
        lock (_failureGate)
        {
            if (_failure is { } currentFailure)
            {
                throw new ReceiveIngestException(currentFailure);
            }
        }
    }

    public Task RetryAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var resume = false;
        lock (_failureGate)
        {
            if (_failure is not null)
            {
                _failure = null;
                _failureSignal = new TaskCompletionSource<Exception>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                resume = true;
            }
        }

        if (resume)
        {
            _resume.Release();
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        _stop.Cancel();
        _resume.Release();
        try { await _consumer.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _resume.Dispose(); _stop.Dispose();
    }

    private async Task ConsumeAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
        {
            if (item is BarrierWork barrier)
            {
                if (Volatile.Read(ref _failure) is not null) await WaitUntilResumedAsync().ConfigureAwait(false);
                barrier.Completion.TrySetResult();
                continue;
            }
            if (item is SynchronizationWork synchronization)
            {
                synchronization.Context.Complete();
                continue;
            }
            var message = (MessageWork)item;
            while (true)
            {
                try
                {
                    var stored = await _store.StoreAsync(message.Envelope, _stop.Token).ConfigureAwait(false);
                    Dequeue(message.Size);
                    RaiseCommitted(stored, message.Envelope);
                    break;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    var newlyPaused = false;
                    lock (_failureGate)
                    {
                        if (_failure is null)
                        {
                            _failure = exception;
                            _failureSignal.TrySetResult(exception);
                            newlyPaused = true;
                        }
                    }
                    if (newlyPaused) RaiseFailed(exception);
                    await WaitUntilResumedAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task WaitUntilResumedAsync()
    {
        while (Volatile.Read(ref _failure) is not null) await _resume.WaitAsync(_stop.Token).ConfigureAwait(false);
    }
    private void Queue(IncomingMessageEnvelope envelope)
    {
        var size = EstimateSize(envelope);
        Interlocked.Increment(ref _queued);
        Interlocked.Add(ref _queuedBytes, size);
        if (!_queue.Writer.TryWrite(new MessageWork(envelope, size)))
        {
            Dequeue(size);
            throw new InvalidOperationException("Incoming message queue is closed.");
        }
    }
    private async Task WriteAsync(IncomingMessageEnvelope envelope, long size, CancellationToken cancellationToken)
    {
        try
        {
            await _queue.Writer.WriteAsync(new MessageWork(envelope, size), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Dequeue(size);
            throw;
        }
    }
    private void Dequeue(long size)
    {
        Interlocked.Decrement(ref _queued);
        Interlocked.Add(ref _queuedBytes, -size);
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    internal Task CompleteSynchronizationAsync(IncomingSynchronization context)
    {
        ThrowIfDisposed();
        if (!_queue.Writer.TryWrite(new SynchronizationWork(context)))
            throw new InvalidOperationException("Incoming message queue is closed.");
        return context.Completion;
    }

    private void RaiseCommitted(StoredIncomingMessage stored, IncomingMessageEnvelope envelope)
    {
        try
        {
            MessageCommitted?.Invoke(this, new IncomingMessageCommitEvent(stored)
            {
                SessionId = envelope.SessionId, ReceivedUtc = envelope.ReceivedUtc,
                Category = envelope.Message is ContactMessage ? IncomingMessageCategory.Private : IncomingMessageCategory.Channel,
                InitialSynchronization = envelope.InitialSynchronization,
            });
        }
        catch { }
    }
    private void RaiseFailed(Exception exception)
    {
        try { Failed?.Invoke(this, new MessageIngestorErrorEventArgs(exception)); } catch { }
    }
    private abstract record WorkItem;
    private sealed record MessageWork(IncomingMessageEnvelope Envelope, long Size) : WorkItem;
    private sealed record SynchronizationWork(IncomingSynchronization Context) : WorkItem;
    private sealed record BarrierWork(TaskCompletionSource Completion) : WorkItem;
    private static IncomingMessageEnvelope Copy(IncomingMessageEnvelope e) => e with { Message = Copy(e.Message), UnknownChannelIdentity = e.UnknownChannelIdentity?.ToArray() };
    private static ReceivedMessage Copy(ReceivedMessage m) => m switch
    {
        ContactMessage x => x with { ContactPublicKeyPrefix = x.ContactPublicKeyPrefix.ToArray(), SenderPrefix = x.SenderPrefix.ToArray() },
        ChannelDataMessage x => x with { Data = x.Data.ToArray() },
        ChannelMessage x => x with { }, _ => throw new NotSupportedException($"Unsupported incoming message type '{m.GetType().Name}'.")
    };
    private static long EstimateSize(IncomingMessageEnvelope envelope) => envelope.Message switch
    {
        ContactMessage message => message.ContactPublicKeyPrefix.Length + message.SenderPrefix.Length +
            sizeof(long) + System.Text.Encoding.UTF8.GetByteCount(message.Text),
        ChannelMessage message => sizeof(byte) + sizeof(long) + System.Text.Encoding.UTF8.GetByteCount(message.Text),
        ChannelDataMessage message => sizeof(byte) + sizeof(ushort) + message.Data.Length,
        _ => 0,
    } + (envelope.UnknownChannelIdentity?.Length ?? 0) + 64;
}
