namespace MeshCoreMessenger.Core.Domain;

public enum AckExpectation { LegacyUnknown = 0, NotExpected = 1, Expected = 2 }

/// <summary>Immutable captured recipient; channel identity is a fingerprint, never a secret.</summary>
public sealed record OutgoingRecipient(
    ConversationKind Kind,
    ReadOnlyMemory<byte> Identity,
    Guid? ChannelBindingId = null,
    byte? Slot = null,
    int? BindingGeneration = null);

/// <summary>OperationId is also MessageId. Reusing it with different content or ownership is rejected.</summary>
public sealed record PrepareOutgoingMessage(
    Guid OperationId, Guid NodeId, Guid SessionId, Guid ConversationId,
    OutgoingRecipient Recipient, string OriginalText, string TransmissionText,
    int MaxUtf8Bytes, DateTimeOffset PreparedUtc);

public sealed record OutgoingAttemptSnapshot(
    Guid Id, Guid MessageId, Guid? SessionId, int AttemptNumber, SendAttemptState State,
    AckExpectation AckExpectation, DateTimeOffset StartedUtc, DateTimeOffset? AcceptedUtc,
    DateTimeOffset? CompletedUtc, long? WireTimestamp, ReadOnlyMemory<byte>? ExpectedAck,
    int? RoundTripMilliseconds, string? ErrorCode);

public sealed record PreparedOutgoingMessage(Guid MessageId, long LocalSequence, OutgoingAttemptSnapshot Attempt);

/// <summary>Compare-and-set transition of one exact attempt, never the latest message in a chat.</summary>
public sealed record OutgoingAttemptTransition(
    Guid NodeId, Guid MessageId, Guid AttemptId, Guid SessionId,
    SendAttemptState ExpectedState, SendAttemptState State, DateTimeOffset AtUtc,
    AckExpectation AckExpectation = AckExpectation.LegacyUnknown,
    long? WireTimestamp = null, ReadOnlyMemory<byte>? ExpectedAck = null,
    int? RoundTripMilliseconds = null, string? ErrorCode = null);

/// <summary>Post-commit invalidation, distinct from incoming/unread events. Reread history for current state.</summary>
public sealed record OutgoingMessageCommit(Guid NodeId, Guid ConversationId, Guid MessageId, bool Inserted);

/// <summary>Persisted immutable send capture. TransmissionText is never reprocessed on a lease.</summary>
public sealed record StoredOutgoingMessage(Guid MessageId, Guid NodeId, Guid ConversationId, Guid? SessionId,
    OutgoingRecipient Recipient, string OriginalText, string TransmissionText);
