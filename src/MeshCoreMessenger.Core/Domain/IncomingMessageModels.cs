using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Domain;

public sealed record IncomingMessageEnvelope(
    Guid EventId,
    Guid SessionId,
    Guid NodeId,
    ReceivedMessage Message,
    DateTimeOffset ReceivedUtc,
    ChannelBindingRecord? StableChannelBinding,
    byte[]? UnknownChannelIdentity);

public sealed record StoredIncomingMessage(
    Guid MessageId,
    Guid EventId,
    Guid ConversationId,
    long LocalSequence,
    bool Inserted);

public sealed record IncomingMessageCommitEvent(StoredIncomingMessage Message);

public sealed class MessageIngestorErrorEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}
