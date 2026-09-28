using MeshCoreSharp;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Domain;

public enum CompanionSessionState
{
    Created,
    Connecting,
    Identifying,
    Identified,
    Stopping,
    Stopped,
    Failed,
}

public enum CompanionSessionEventKind
{
    ConnectionStateChanged,
    BackgroundError,
    PacketReceived,
    PushPacketReceived,
    UnhandledPacketReceived,
    MessageReceived,
    AdvertisementReceived,
    EventBarrier,
}

public sealed record LocalNodeIdentity(ReadOnlyMemory<byte> PublicKey, string Name)
{
    public string PublicKeyHex => Convert.ToHexString(PublicKey.Span);
}

public sealed record CompanionSessionStartResult(
    Guid SessionId,
    long Generation,
    Guid NodeId,
    LocalNodeIdentity Node);

internal sealed class CompanionSessionLifecycleEventArgs : EventArgs
{
    public required Guid SessionId { get; init; }
    public required long Generation { get; init; }
    public CompanionSessionState? SessionState { get; init; }
    public MeshCoreConnectionState? ConnectionState { get; init; }
    public Exception? Error { get; init; }
}

public sealed record CompanionSessionEvent
{
    public required Guid SessionId { get; init; }
    public required long Generation { get; init; }
    public required DateTimeOffset OccurredUtc { get; init; }
    public required CompanionSessionEventKind Kind { get; init; }
    public MeshCoreConnectionState? PreviousConnectionState { get; init; }
    public MeshCoreConnectionState? CurrentConnectionState { get; init; }
    public string? ErrorType { get; init; }
    public string? ErrorMessage { get; init; }
    public byte? RawPacketType { get; init; }
    public ReadOnlyMemory<byte> RawFrame { get; init; }
    public ReceivedMessage? Message { get; init; }
    public AdvertisementInfo? Advertisement { get; init; }
    internal TaskCompletionSource? BarrierCompletion { get; init; }
}
