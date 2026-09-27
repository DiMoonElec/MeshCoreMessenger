using System.Threading.Channels;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Application;

/// <summary>Single-consumer durable ingress. Event callbacks enqueue copied DTOs and never await SQLite.</summary>
public sealed class MessageIngestor : IAsyncDisposable
{
    private const int OverloadMessageCount = 1_000;
    private const long OverloadByteCount = 16L * 1024 * 1024;
    private readonly IIncomingMessageStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly SemaphoreSlim _resume = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _consumer;
    private Exception? _failure;
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
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new BarrierWork(completion), cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task RetryAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _failure, null) is not null) _resume.Release();
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
            var message = (MessageWork)item;
            while (true)
            {
                try
                {
                    var stored = await _store.StoreAsync(message.Envelope, _stop.Token).ConfigureAwait(false);
                    Dequeue(message.Size);
                    RaiseCommitted(stored);
                    break;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    if (Interlocked.CompareExchange(ref _failure, exception, null) is null) RaiseFailed(exception);
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
    private void RaiseCommitted(StoredIncomingMessage stored)
    {
        try { MessageCommitted?.Invoke(this, new IncomingMessageCommitEvent(stored)); } catch { }
    }
    private void RaiseFailed(Exception exception)
    {
        try { Failed?.Invoke(this, new MessageIngestorErrorEventArgs(exception)); } catch { }
    }
    private abstract record WorkItem;
    private sealed record MessageWork(IncomingMessageEnvelope Envelope, long Size) : WorkItem;
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
