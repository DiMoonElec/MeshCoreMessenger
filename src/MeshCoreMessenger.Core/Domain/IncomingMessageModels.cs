using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Domain;

public sealed record IncomingMessageEnvelope(
    Guid EventId,
    Guid SessionId,
    Guid NodeId,
    ReceivedMessage Message,
    DateTimeOffset ReceivedUtc,
    ChannelBindingRecord? StableChannelBinding,
    byte[]? UnknownChannelIdentity)
{
    public IncomingSynchronization? InitialSynchronization { get; init; }
}

public sealed record StoredIncomingMessage(
    Guid MessageId,
    Guid EventId,
    Guid NodeId,
    Guid ConversationId,
    long LocalSequence,
    bool Inserted);

public enum IncomingMessageCategory { Private, Channel }

/// <summary>In-memory reception context; completion follows commits, never device timestamps.</summary>
public sealed class IncomingSynchronization(Guid sessionId, Guid nodeId)
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Guid SessionId { get; } = sessionId;
    public Guid NodeId { get; } = nodeId;
    public Task<bool> Completion => _completion.Task;
    internal void Complete() => _completion.TrySetResult(true);
    internal void Abort() => _completion.TrySetResult(false);
}

public sealed record IncomingMessageCommitEvent(StoredIncomingMessage Message)
{
    public Guid SessionId { get; init; }
    public DateTimeOffset ReceivedUtc { get; init; }
    public IncomingMessageCategory Category { get; init; }
    public IncomingSynchronization? InitialSynchronization { get; init; }
}

public sealed class MessageIngestorErrorEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}
