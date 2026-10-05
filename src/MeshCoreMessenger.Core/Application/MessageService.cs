using System.Buffers.Binary;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Application;

public abstract record TextSendRequest(Guid NodeId, Guid SessionId, long Generation,
    OutgoingRecipient Recipient, DraftCapture Draft, OutgoingTextOptions Options);
public sealed record ChannelSendRequest(Guid NodeId, Guid SessionId, long Generation,
    OutgoingRecipient Recipient, DraftCapture Draft, OutgoingTextOptions Options)
    : TextSendRequest(NodeId, SessionId, Generation, Recipient, Draft, Options);
public sealed record PrivateSendRequest(Guid NodeId, Guid SessionId, long Generation,
    OutgoingRecipient Recipient, DraftCapture Draft, OutgoingTextOptions Options)
    : TextSendRequest(NodeId, SessionId, Generation, Recipient, Draft, Options);
public sealed record PrivateSendOutcome(Guid MessageId, SendAttemptState State);
public sealed record ChannelRepeatRequest(Guid NodeId, Guid SessionId, long Generation,
    Guid MessageId, int ExpectedAttemptNumber, OutgoingRecipient Recipient);
public sealed record ChannelSendOutcome(Guid MessageId, SendAttemptState State);

public interface IMessageService
{
    Task<ChannelSendOutcome> SendChannelAsNewAsync(ChannelRepeatRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task<ChannelSendOutcome> RepeatChannelAsync(ChannelRepeatRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task<IReadOnlyList<OutgoingRecipient>> GetChannelTargetsAsync(Guid nodeId, ReadOnlyMemory<byte> fingerprint,
        CancellationToken cancellationToken = default);
    Task<PrivateSendOutcome> SendPrivateAsync(PrivateSendRequest request,
        Func<DraftCapture, Task>? transferred = null, CancellationToken cancellationToken = default);
    Task<ChannelSendOutcome> SendChannelAsync(ChannelSendRequest request,
        Func<DraftCapture, Task>? transferred = null, CancellationToken cancellationToken = default);
}

/// <summary>One explicit send, owned by its captured session through the durable final status.</summary>
public sealed class MessageService(ISessionCommandGateway gateway, IOutgoingMessageStore messages,
    IDirectoryStore directories, IConversationDirectoryReader directory, IDraftBuffer drafts,
    IOutgoingTextProcessor processor, TimeProvider timeProvider, IDraftStore draftStore, ConversationOperationGuard operations) : IMessageService
{
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private long _lastChannelTimestamp;

    public async Task<ChannelSendOutcome> RepeatChannelAsync(ChannelRepeatRequest request, CancellationToken cancellationToken = default)
        => await SendCapturedChannelAsync(request, false, cancellationToken).ConfigureAwait(false);

    public Task<ChannelSendOutcome> SendChannelAsNewAsync(ChannelRepeatRequest request, CancellationToken cancellationToken = default) =>
        SendCapturedChannelAsync(request, true, cancellationToken);

    private async Task<ChannelSendOutcome> SendCapturedChannelAsync(ChannelRepeatRequest request, bool asNew, CancellationToken cancellationToken)
    {
        var stored = await messages.GetAsync(request.NodeId, request.MessageId, cancellationToken).ConfigureAwait(false);
        if (stored.Recipient.Kind != ConversationKind.Channel)
            throw new InvalidOperationException("Only captured channel messages are supported.");
        var capture = new DraftCapture(new(request.NodeId, stored.ConversationId, ConversationKind.Channel,
            stored.Recipient.Identity.ToArray()), stored.OriginalText, 0);
        return await SendAsync(new ChannelSendRequest(request.NodeId, request.SessionId, request.Generation,
            request.Recipient, capture, new()), true, null, cancellationToken, stored, request.ExpectedAttemptNumber, asNew).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutgoingRecipient>> GetChannelTargetsAsync(Guid nodeId, ReadOnlyMemory<byte> fingerprint,
        CancellationToken cancellationToken = default)
    {
        var copy = fingerprint.ToArray();
        var channel = await directory.GetChannelDetailsAsync(nodeId, copy, cancellationToken).ConfigureAwait(false);
        if (channel is null) return [];
        var targets = new List<OutgoingRecipient>();
        foreach (var slot in channel.ActiveSlots)
        {
            var binding = await directories.GetActiveChannelBindingAsync(nodeId, slot, cancellationToken).ConfigureAwait(false);
            if (binding is not null && binding.ChannelId == channel.Id && binding.UnboundUtc is null)
                targets.Add(new(ConversationKind.Channel, copy, binding.Id, slot, binding.Generation));
        }
        return targets;
    }

    public Task<ChannelSendOutcome> SendChannelAsync(ChannelSendRequest request,
        Func<DraftCapture, Task>? transferred = null, CancellationToken cancellationToken = default) =>
        SendAsync(request, isChannel: true, transferred, cancellationToken);

    public async Task<PrivateSendOutcome> SendPrivateAsync(PrivateSendRequest request,
        Func<DraftCapture, Task>? transferred = null, CancellationToken cancellationToken = default)
    {
        var outcome = await SendAsync(request, isChannel: false, transferred, cancellationToken).ConfigureAwait(false);
        return new(outcome.MessageId, outcome.State);
    }

    private async Task<ChannelSendOutcome> SendAsync(TextSendRequest request, bool isChannel,
        Func<DraftCapture, Task>? transferred, CancellationToken cancellationToken,
        StoredOutgoingMessage? repeat = null, int expectedAttemptNumber = 0, bool asNew = false)
    {
        if (!await _singleFlight.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("An outgoing send is already in progress.");
        SessionCommandLease? lease = null;
        var observing = false;
        IDisposable? activity = null;
        try
        {
            var recipient = request.Recipient with { Identity = request.Recipient.Identity.ToArray() };
            var capture = request.Draft with { Target = request.Draft.Target with { Identity = request.Draft.Target.Identity.ToArray() } };
            var kind = isChannel ? ConversationKind.Channel : ConversationKind.Contact;
            if (recipient.Kind != kind || capture.Target.NodeId != request.NodeId || capture.Target.Kind != kind ||
                !capture.Target.Identity.AsSpan().SequenceEqual(recipient.Identity.Span))
                throw new InvalidOperationException("Draft and recipient ownership differ.");
            activity = operations.BeginSend(request.NodeId, kind, recipient.Identity);
            lease = gateway.Acquire(request.NodeId, isChannel
                ? new ChannelCommandTarget(recipient) : new ContactCommandTarget(recipient.Identity), cancellationToken);
            if (lease.Owner.SessionId != request.SessionId || lease.Owner.Generation != request.Generation)
                throw new InvalidOperationException("The selected session has changed.");
            var processed = repeat is null ? processor.Process(capture.Text, new(isChannel, lease.Owner.SenderName), request.Options) : null;
            var transmission = repeat?.TransmissionText ?? processed!.TransmissionText;
            var budget = isChannel ? MeshCoreSharp.TextMessageValidator.GetChannelTextLimit(lease.Owner.SenderName) : MeshCoreSharp.Protocol.ProtocolLimits.MaxTextBytes;
            if (!(processed?.Validation.IsValid ?? MeshCoreSharp.TextMessageValidator.Validate(transmission, budget).IsValid) || string.IsNullOrWhiteSpace(transmission))
                throw new ArgumentException("Message text is empty or invalid.");
            return await lease.RunAsync(async (owned, token) =>
            {
                // A not-yet-materialized directory conversation is created by the local draft store.
                PreparedOutgoingMessage prepared;
                if (repeat is not null && !asNew)
                    prepared = await messages.PrepareChannelRepeatAsync(new(request.NodeId, repeat.MessageId, owned.Owner.SessionId,
                        recipient, expectedAttemptNumber, timeProvider.GetUtcNow()), token).ConfigureAwait(false);
                else
                {
                    if (repeat is null) await drafts.FlushAsync(capture.Target, token).ConfigureAwait(false);
                    var draft = repeat is null ? await draftStore.GetAsync(capture.Target, token).ConfigureAwait(false) : null;
                    var conversation = capture.Target.ConversationId ?? draft?.ConversationId
                        ?? throw new InvalidOperationException("The conversation has not been materialized.");
                    prepared = await messages.PrepareAsync(new(Guid.NewGuid(), owned.Owner.NodeId, owned.Owner.SessionId,
                        conversation, recipient, repeat?.OriginalText ?? processed!.OriginalText, transmission,
                        processed?.Validation.MaxUtf8Bytes ?? budget, timeProvider.GetUtcNow()), token).ConfigureAwait(false);
                }
                await owned.BindOutgoingAsync(prepared.MessageId, prepared.Attempt.Id).ConfigureAwait(false);
                if (repeat is null && await drafts.ClearTransferredAsync(capture, token).ConfigureAwait(false) && transferred is not null)
                    await transferred(capture).ConfigureAwait(false);
                uint? channelTimestamp = null;
                if (isChannel)
                {
                    var attempts = await messages.GetAttemptsAsync(request.NodeId, repeat?.MessageId ?? prepared.MessageId, token).ConfigureAwait(false);
                    var prior = attempts.Max(attempt => attempt.WireTimestamp ?? 0);
                    if (repeat is null || asNew)
                        prior = Math.Max(prior, await messages.GetLatestChannelTimestampAsync(request.NodeId, token).ConfigureAwait(false));
                    // Delivery repeat retains packet identity; sending as new must exceed the source timestamp.
                    var next = repeat is not null && !asNew
                        ? attempts.First(attempt => attempt.WireTimestamp is not null).WireTimestamp!.Value
                        : Math.Max(timeProvider.GetUtcNow().ToUnixTimeSeconds(), Math.Max(prior, _lastChannelTimestamp) + 1);
                    channelTimestamp = checked((uint)next);
                    _lastChannelTimestamp = Math.Max(_lastChannelTimestamp, next);
                }
                if (!await owned.TransitionAsync(SendAttemptState.Prepared, SendAttemptState.Sending,
                    wireTimestamp: channelTimestamp).ConfigureAwait(false))
                    throw new InvalidOperationException("Sending was not committed.");
                ChannelMessageSendResult? channelResult = null;
                TextMessageSendResult? privateResult = null;
                try
                {
                    if (isChannel) channelResult = await owned.SendChannelTextAsync(channelTimestamp).ConfigureAwait(false);
                    else
                    {
                        privateResult = await owned.SendTextAsync().ConfigureAwait(false);
                        ObserveFailure(privateResult.Delivery);
                    }
                }
                catch (OutgoingPersistenceException) { throw; }
                catch (Exception error)
                {
                    var state = !owned.WasInvoked || error is MeshCoreCommandException ? SendAttemptState.Failed : SendAttemptState.Unknown;
                    if (!await owned.TransitionAsync(SendAttemptState.Sending, state, errorCode: error.GetType().Name).ConfigureAwait(false))
                        throw new InvalidOperationException("Outgoing status changed before the result was committed.", error);
                    return new ChannelSendOutcome(prepared.MessageId, state);
                }
                var tag = privateResult?.Accepted.ExpectedAck ?? 0;
                byte[]? expectedAck = null;
                if (tag != 0)
                {
                    expectedAck = new byte[sizeof(uint)];
                    BinaryPrimitives.WriteUInt32LittleEndian(expectedAck, tag);
                }
                if (!await owned.TransitionAsync(SendAttemptState.Sending, SendAttemptState.Accepted,
                    tag == 0 ? AckExpectation.NotExpected : AckExpectation.Expected,
                    privateResult?.Timestamp ?? channelResult!.Timestamp, expectedAck).ConfigureAwait(false))
                    throw new InvalidOperationException("Outgoing status changed before acceptance was committed.");
                if (privateResult is not null && tag != 0)
                {
                    // Register before returning to the UI. Delivery may already be complete;
                    // its terminal write can only follow the committed Accepted write above.
                    _ = owned.ObserveAsync((observer, observerToken) => RecordDeliveryAsync(observer, privateResult, observerToken));
                    observing = true;
                }
                return new ChannelSendOutcome(prepared.MessageId, SendAttemptState.Accepted);
            }).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (lease is not null)
                {
                    var completion = lease.DisposeAsync().AsTask();
                    // FinishAsync waits for registered observers; the session owns this lease
                    // until completion, including shutdown and durable-write failure barriers.
                    if (observing)
                    {
                        var retained = activity;
                        activity = null;
                        ObserveFailure(ReleaseActivityAsync(completion, retained));
                    }
                    else await completion.ConfigureAwait(false);
                }
            }
            finally { activity?.Dispose(); _singleFlight.Release(); }
        }
    }

    private static async Task ReleaseActivityAsync(Task completion, IDisposable? activity)
    {
        try { await completion.ConfigureAwait(false); }
        finally { activity?.Dispose(); }
    }

    private static async Task RecordDeliveryAsync(SessionCommandLease lease, TextMessageSendResult sent, CancellationToken token)
    {
        SendAttemptState state;
        string? errorCode = null;
        try
        {
            var delivery = await sent.Delivery.WaitAsync(token).ConfigureAwait(false);
            state = delivery.Status switch
            {
                MessageDeliveryStatus.Confirmed when delivery.Acknowledgement?.Ack == sent.Accepted.ExpectedAck => SendAttemptState.Delivered,
                MessageDeliveryStatus.TimedOut => SendAttemptState.Unconfirmed,
                _ => SendAttemptState.Unknown,
            };
            if (state == SendAttemptState.Delivered)
            {
                await lease.RecordAcknowledgementAsync(delivery.Acknowledgement!.Ack,
                    delivery.Acknowledgement.RoundTripTimeMilliseconds).ConfigureAwait(false);
                if (await lease.IsDeliveryCommittedAsync().ConfigureAwait(false)) return;
                state = SendAttemptState.Unknown;
                errorCode = "AmbiguousAcknowledgement";
            }
            if (state == SendAttemptState.Unknown) errorCode ??= "UnexpectedDeliveryResult";
        }
        catch (Exception error)
        {
            state = SendAttemptState.Unknown;
            errorCode = error.GetType().Name;
        }
        if (!await lease.TransitionAsync(SendAttemptState.Accepted, state,
            errorCode: errorCode).ConfigureAwait(false) &&
            !await lease.IsDeliveryCommittedAsync().ConfigureAwait(false))
            throw new InvalidOperationException("Delivery status changed before its result was committed.");
    }

    private static void ObserveFailure(Task task) => _ = task.ContinueWith(completed => { _ = completed.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
