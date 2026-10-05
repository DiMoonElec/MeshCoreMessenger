using System.Buffers.Binary;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Application;

/// <summary>Bounded session-owned jobs; each attempt uses a fresh lease in the original scope.</summary>
public sealed class PrivateDeliveryCoordinator(IOutgoingMessageStore messages, IDurableOutgoingWrites outgoing, TimeProvider timeProvider, IDirectoryStore directory)
{
    public const int MaximumJobs = 32;
    public const int MaximumJobsPerContact = 8;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid Node, Guid Session, long Generation, string Contact), QueueState> _queues = [];
    private readonly SemaphoreSlim _immediate = new(1, 1);
    private int _pending;
    public int PendingCount { get { lock (_gate) return _pending; } }

    internal Reservation Reserve(SessionCommandLease parent)
    {
        if (parent.Target is not ContactCommandTarget contact) throw new InvalidOperationException("Private queue requires a contact.");
        lock (_gate)
        {
            var key = (parent.Owner.NodeId, parent.Owner.SessionId, parent.Owner.Generation, Convert.ToHexString(contact.PublicKey.Span));
            _queues.TryGetValue(key, out var queue);
            if (_pending >= MaximumJobs || queue?.Count >= MaximumJobsPerContact)
                throw new InvalidOperationException("The private delivery queue is full.");
            queue ??= new();
            var reservation = new Reservation(queue.Tail, () =>
            {
                lock (_gate)
                {
                    _pending--;
                    if (--queue.Count == 0) _queues.Remove(key);
                }
            });
            queue.Count++;
            queue.Tail = reservation.Completion;
            _queues[key] = queue;
            _pending++;
            return reservation;
        }
    }

    internal void Start(SessionCommandLease parent, PreparedOutgoingMessage prepared, PrivateRetryPolicy policy,
        Reservation reservation, IDisposable activity)
    {
        _ = parent.ObserveAsync(async (owned, token) =>
        {
            try
            {
                await reservation.Predecessor.WaitAsync(token).ConfigureAwait(false);
                await ExecuteAsync(owned, prepared, policy, token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await AbortPreparedAsync(owned, prepared, error.GetType().Name).ConfigureAwait(false);
                throw;
            }
            finally { activity.Dispose(); reservation.Dispose(); }
        });
    }

    internal async Task AbortPreparedAsync(SessionCommandLease parent, PreparedOutgoingMessage prepared, string errorCode)
    {
        // These writes remain queued even while persistence is paused; no command is retried.
        try
        {
            await outgoing.FinishPrivateCycleAsync(new(parent.Owner.NodeId, prepared.MessageId,
                parent.Owner.SessionId, PrivateDeliveryState.Unknown, errorCode), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            var attempts = await messages.GetAttemptsAsync(parent.Owner.NodeId, prepared.MessageId, CancellationToken.None).ConfigureAwait(false);
            var latest = attempts[^1];
            if (latest.State == SendAttemptState.Prepared)
                await outgoing.SaveAsync(new(parent.Owner.NodeId, prepared.MessageId, latest.Id, parent.Owner.SessionId,
                    SendAttemptState.Prepared, SendAttemptState.Failed,
                    MaxUtc(latest.StartedUtc, timeProvider.GetUtcNow()), ErrorCode: errorCode), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task ExecuteAsync(SessionCommandLease parent, PreparedOutgoingMessage original, PrivateRetryPolicy policy, CancellationToken token)
    {
        var signal = new ChangeSignal();
        void Changed(object? sender, OutgoingMessageCommit commit)
        {
            if (commit.NodeId == parent.Owner.NodeId && commit.MessageId == original.MessageId) signal.Pulse();
        }
        messages.MessageCommitted += Changed;
        try
        {
            PrivateRetryPlan? plan = null;
            for (var index = 0; index < (plan?.Steps.Count ?? 1); index++)
            {
                token.ThrowIfCancellationRequested();
                SessionCommandLease? attempt = null;
                TextMessageSendResult? sent = null;
                try
                {
                    await _immediate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (await IsDeliveredAsync(parent, original.MessageId).ConfigureAwait(false)) return;
                        await parent.ValidateRecipientAsync().ConfigureAwait(false);
                        var contact = await parent.GetContactAsync().ConfigureAwait(false);
                        if (parent.Target is not ContactCommandTarget target || !contact.PublicKey.Span.SequenceEqual(target.PublicKey.Span) ||
                            contact.AdvertisementType != AdvertisementType.Chat)
                            throw new InvalidOperationException("Node returned a different or unsupported contact.");
                        var route = new PrivateRouteSnapshot(contact.OutPathLength, contact.OutPath, timeProvider.GetUtcNow());
                        if (index == 0)
                        {
                            plan = PrivateRetryPlan.Create(route.Kind == PrivateRouteKind.Flood, policy);
                            await messages.BeginPrivateCycleAsync(new(parent.Owner.NodeId, original.MessageId,
                                parent.Owner.SessionId, policy, route, timeProvider.GetUtcNow()), token).ConfigureAwait(false);
                        }
                        var step = plan!.Steps[index];
                        var flood = step.Phase != PrivateDeliveryPhase.KnownRoute;
                        if (await IsDeliveredAsync(parent, original.MessageId).ConfigureAwait(false)) return;
                        if (step.ResetRouteBeforeSend || flood && route.Kind != PrivateRouteKind.Flood)
                        {
                            contact = await OwnedContactRouteReset.ResetAsync(parent, directory, timeProvider, token).ConfigureAwait(false);
                            route = new(contact.OutPathLength, contact.OutPath, timeProvider.GetUtcNow());
                        }
                        if (await IsDeliveredAsync(parent, original.MessageId).ConfigureAwait(false)) return;
                        if ((route.Kind == PrivateRouteKind.Flood) != flood)
                            throw new InvalidOperationException("Current contact route does not match the delivery phase.");
                        if (await IsDeliveredAsync(parent, original.MessageId).ConfigureAwait(false)) return;
                        var utc = timeProvider.GetUtcNow();
                        var pc = utc.ToOffset(TimeZoneInfo.Local.GetUtcOffset(utc));
                        var next = await messages.PreparePrivateAttemptAsync(new(Guid.NewGuid(), parent.Owner.NodeId,
                            original.MessageId, parent.Owner.SessionId, index, route, pc, TimeZoneInfo.Local.Id), token).ConfigureAwait(false);
                        var prepared = next.Message;
                        var capture = next.Capture;
                        if (await IsDeliveredAsync(parent, original.MessageId).ConfigureAwait(false)) return;
                        attempt = parent.CreateAttemptLease();
                        await attempt.BindOutgoingAsync(prepared.MessageId, prepared.Attempt.Id).ConfigureAwait(false);
                        if (!await attempt.TransitionAsync(SendAttemptState.Prepared, SendAttemptState.Sending).ConfigureAwait(false)) return;
                        if (await IsDeliveredAsync(parent, original.MessageId).ConfigureAwait(false)) return;
                        try
                        {
                            sent = await attempt.SendTextAsync(capture.WireMessage.Timestamp, capture.WireAttempt).ConfigureAwait(false);
                        }
                        catch (Exception error) when (error is not OutgoingPersistenceException)
                        {
                            await attempt.TransitionAsync(SendAttemptState.Sending,
                                !attempt.WasInvoked || error is MeshCoreCommandException ? SendAttemptState.Failed : SendAttemptState.Unknown,
                                errorCode: error.GetType().Name).ConfigureAwait(false);
                            return;
                        }
                        var tag = new byte[sizeof(uint)];
                        BinaryPrimitives.WriteUInt32LittleEndian(tag, sent.Accepted.ExpectedAck);
                        await attempt.TransitionAsync(SendAttemptState.Sending, SendAttemptState.Accepted,
                            sent.Accepted.ExpectedAck == 0 ? AckExpectation.NotExpected : AckExpectation.Expected,
                            sent.Timestamp, sent.Accepted.ExpectedAck == 0 ? null : tag,
                            modeReportedByMsgSent: sent.Accepted.IsFlood,
                            ackDeadlineUtc: sent.Accepted.ExpectedAck == 0 ? null : attempt.EstimateAcknowledgementDeadline(sent.Accepted.SuggestedTimeoutMilliseconds)).ConfigureAwait(false);
                        if (sent.Accepted.ExpectedAck == 0) return;
                        if (flood != sent.Accepted.IsFlood)
                        {
                            await attempt.TransitionAsync(SendAttemptState.Accepted, SendAttemptState.Unknown,
                                errorCode: "RouteModeChangedBeforeSend").ConfigureAwait(false);
                            return;
                        }
                    }
                    finally { _immediate.Release(); }

                    while (true)
                    {
                        var changed = signal.Current;
                        if (await IsDeliveredAsync(parent, original.MessageId).ConfigureAwait(false)) return;
                        var ready = await Task.WhenAny(sent!.Delivery, changed).WaitAsync(token).ConfigureAwait(false);
                        if (ready == changed) { signal.Consume(changed); continue; }
                        MessageDeliveryResult delivery;
                        try { delivery = await sent.Delivery.ConfigureAwait(false); }
                        catch (Exception error)
                        {
                            await attempt!.TransitionAsync(SendAttemptState.Accepted, SendAttemptState.Unknown, errorCode: error.GetType().Name).ConfigureAwait(false);
                            return;
                        }
                        if (delivery.Status == MessageDeliveryStatus.Confirmed && delivery.Acknowledgement?.Ack == sent.Accepted.ExpectedAck)
                        {
                            await attempt!.RecordAcknowledgementAsync(delivery.Acknowledgement.Ack, delivery.Acknowledgement.RoundTripTimeMilliseconds).ConfigureAwait(false);
                            if (!await IsDeliveredAsync(parent, original.MessageId).ConfigureAwait(false))
                                await attempt.TransitionAsync(SendAttemptState.Accepted, SendAttemptState.Unknown, errorCode: "UnresolvedAcknowledgement").ConfigureAwait(false);
                            return;
                        }
                        if (delivery.Status != MessageDeliveryStatus.TimedOut)
                        {
                            await attempt!.TransitionAsync(SendAttemptState.Accepted, SendAttemptState.Unknown, errorCode: "UnexpectedDeliveryResult").ConfigureAwait(false);
                            return;
                        }
                        await attempt!.TransitionAsync(SendAttemptState.Accepted, SendAttemptState.Unconfirmed).ConfigureAwait(false);
                        break;
                    }
                }
                finally
                {
                    if (attempt is not null)
                    {
                        await attempt.CancelAttemptAsync().ConfigureAwait(false); // Cancel only this ACK waiter, never the parent job.
                        if (sent is not null) { try { await sent.Delivery.ConfigureAwait(false); } catch { /* Already recorded/owned cancellation. */ } }
                        await attempt.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        finally { messages.MessageCommitted -= Changed; }
    }

    private async Task<bool> IsDeliveredAsync(SessionCommandLease parent, Guid message)
    {
        var cycle = await messages.GetPrivateCycleAsync(parent.Owner.NodeId, message, CancellationToken.None).ConfigureAwait(false);
        if (cycle?.State == PrivateDeliveryState.Delivered) return true;
        return (await messages.GetAttemptsAsync(parent.Owner.NodeId, message, CancellationToken.None).ConfigureAwait(false))
            .Any(item => item.State == SendAttemptState.Delivered);
    }
    private static DateTimeOffset MaxUtc(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;
    private sealed class QueueState { public int Count; public Task Tail = Task.CompletedTask; }
    internal sealed class Reservation(Task predecessor, Action release) : IDisposable
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action? _release = release;
        internal Task Predecessor { get; } = predecessor;
        internal Task Completion => _done.Task;
        public void Dispose()
        {
            var action = Interlocked.Exchange(ref _release, null);
            if (action is null) return;
            action();
            // A cancelled queued job must not let its successor jump over the still-running predecessor.
            _ = Predecessor.ContinueWith(_ => _done.TrySetResult(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
    private sealed class ChangeSignal
    {
        private readonly object _gate = new();
        private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Current { get { lock (_gate) return _next.Task; } }
        internal void Pulse() { lock (_gate) _next.TrySetResult(); }
        internal void Consume(Task task) { lock (_gate) if (_next.Task == task) _next = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    }
}
