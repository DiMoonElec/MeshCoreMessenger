using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence;

public interface IOutgoingMessageStore
{
    event EventHandler<OutgoingMessageCommit>? MessageCommitted;
    Task<PreparedOutgoingMessage> PrepareAsync(PrepareOutgoingMessage message, CancellationToken cancellationToken = default);
    Task<PreparedOutgoingMessage> PrepareChannelRepeatAsync(PrepareChannelRepeat repeat, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task<bool> TransitionAsync(OutgoingAttemptTransition transition, CancellationToken cancellationToken = default);
    /// <summary>Returns true when evidence is consumed (including duplicate/ambiguous ACKs); false if metadata is not yet registered.</summary>
    Task<bool> ConfirmAcknowledgementAsync(OutgoingAcknowledgement acknowledgement, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task<StoredOutgoingMessage> GetAsync(Guid nodeId, Guid messageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OutgoingAttemptSnapshot>> GetAttemptsAsync(Guid nodeId, Guid messageId, CancellationToken cancellationToken = default);
    Task<long> GetLatestChannelTimestampAsync(Guid nodeId, CancellationToken cancellationToken = default) => Task.FromResult(0L);
    Task<PrivateDeliveryCycleSnapshot> BeginPrivateCycleAsync(BeginPrivateDeliveryCycle cycle, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task<PreparedPrivateAttempt> PreparePrivateAttemptAsync(PreparePrivateAttempt attempt, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task<PrivateDeliveryCycleSnapshot?> GetPrivateCycleAsync(Guid nodeId, Guid messageId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task<IReadOnlyList<PrivateAttemptCapture>> GetPrivateAttemptCapturesAsync(Guid nodeId, Guid messageId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
