using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Application;

public sealed record ChannelSendRequest(Guid NodeId, Guid SessionId, long Generation,
    OutgoingRecipient Recipient, DraftCapture Draft, OutgoingTextOptions Options);
public sealed record ChannelSendOutcome(Guid MessageId, SendAttemptState State);

public interface IMessageService
{
    Task<IReadOnlyList<OutgoingRecipient>> GetChannelTargetsAsync(Guid nodeId, ReadOnlyMemory<byte> fingerprint,
        CancellationToken cancellationToken = default);
    Task<ChannelSendOutcome> SendChannelAsync(ChannelSendRequest request,
        Func<DraftCapture, Task>? transferred = null, CancellationToken cancellationToken = default);
}

/// <summary>One explicit send, owned by its captured session through the durable final status.</summary>
public sealed class MessageService(ISessionCommandGateway gateway, IOutgoingMessageStore messages,
    IDirectoryStore directories, IConversationDirectoryReader directory, IDraftBuffer drafts,
    IOutgoingTextProcessor processor, TimeProvider timeProvider, IDraftStore draftStore) : IMessageService
{
    private readonly SemaphoreSlim _singleFlight = new(1, 1);

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

    public async Task<ChannelSendOutcome> SendChannelAsync(ChannelSendRequest request,
        Func<DraftCapture, Task>? transferred = null, CancellationToken cancellationToken = default)
    {
        if (!await _singleFlight.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("An outgoing send is already in progress.");
        try
        {
            var recipient = request.Recipient with { Identity = request.Recipient.Identity.ToArray() };
            var capture = request.Draft with { Target = request.Draft.Target with { Identity = request.Draft.Target.Identity.ToArray() } };
            if (capture.Target.NodeId != request.NodeId || capture.Target.Kind != ConversationKind.Channel ||
                !capture.Target.Identity.AsSpan().SequenceEqual(recipient.Identity.Span))
                throw new InvalidOperationException("Draft and channel ownership differ.");
            await using var lease = gateway.Acquire(request.NodeId, new ChannelCommandTarget(recipient), cancellationToken);
            if (lease.Owner.SessionId != request.SessionId || lease.Owner.Generation != request.Generation)
                throw new InvalidOperationException("The selected session has changed.");
            var processed = processor.Process(capture.Text, new(true, lease.Owner.SenderName), request.Options);
            if (!processed.Validation.IsValid || string.IsNullOrWhiteSpace(processed.TransmissionText))
                throw new ArgumentException("Message text is empty or invalid.");
            return await lease.RunAsync(async (owned, token) =>
            {
                // A not-yet-materialized directory conversation is created by the local draft store.
                await drafts.FlushAsync(capture.Target, token).ConfigureAwait(false);
                var draft = await draftStore.GetAsync(capture.Target, token).ConfigureAwait(false);
                var conversation = capture.Target.ConversationId ?? draft?.ConversationId
                    ?? throw new InvalidOperationException("The channel conversation has not been materialized.");
                var prepared = await messages.PrepareAsync(new(Guid.NewGuid(), owned.Owner.NodeId, owned.Owner.SessionId,
                    conversation, recipient, processed.OriginalText, processed.TransmissionText,
                    processed.Validation.MaxUtf8Bytes!.Value, timeProvider.GetUtcNow()), token).ConfigureAwait(false);
                await owned.BindOutgoingAsync(prepared.MessageId, prepared.Attempt.Id).ConfigureAwait(false);
                if (await drafts.ClearTransferredAsync(capture, token).ConfigureAwait(false) && transferred is not null)
                    await transferred(capture).ConfigureAwait(false);
                if (!await owned.TransitionAsync(SendAttemptState.Prepared, SendAttemptState.Sending).ConfigureAwait(false))
                    throw new InvalidOperationException("Sending was not committed.");
                ChannelMessageSendResult result;
                try { result = await owned.SendChannelTextAsync().ConfigureAwait(false); }
                catch (OutgoingPersistenceException) { throw; }
                catch (Exception error)
                {
                    var state = !owned.WasInvoked || error is MeshCoreCommandException ? SendAttemptState.Failed : SendAttemptState.Unknown;
                    if (!await owned.TransitionAsync(SendAttemptState.Sending, state, errorCode: error.GetType().Name).ConfigureAwait(false))
                        throw new InvalidOperationException("Outgoing status changed before the result was committed.", error);
                    return new ChannelSendOutcome(prepared.MessageId, state);
                }
                if (!await owned.TransitionAsync(SendAttemptState.Sending, SendAttemptState.Accepted,
                    AckExpectation.NotExpected, result.Timestamp).ConfigureAwait(false))
                    throw new InvalidOperationException("Outgoing status changed before acceptance was committed.");
                return new ChannelSendOutcome(prepared.MessageId, SendAttemptState.Accepted);
            }).ConfigureAwait(false);
        }
        finally { _singleFlight.Release(); }
    }
}
