using MeshCoreSharp.Protocol;

namespace MeshCoreMessenger.Core.Domain;

public enum PrivateDeliveryPhase { KnownRoute = 0, Flood = 1, FallbackFlood = 2 }
public enum PrivateRouteKind { Flood = 0, Direct = 1, Path = 2 }
public enum PrivateDeliveryState { Prepared = 0, Active = 1, Delivered = 2, Unconfirmed = 3, Failed = 4, Unknown = 5 }

/// <summary>Configured contact route observed before TX, not a proven radio trace. Unused path bytes are discarded.</summary>
public sealed record PrivateRouteSnapshot
{
    public PrivateRouteSnapshot(byte descriptor, ReadOnlyMemory<byte> path, DateTimeOffset observedUtc)
    {
        Descriptor = descriptor;
        ObservedUtc = observedUtc.ToUniversalTime();
        if (descriptor == byte.MaxValue)
        {
            Kind = PrivateRouteKind.Flood;
            Path = ReadOnlyMemory<byte>.Empty;
            return;
        }
        HashSize = (descriptor >> 6) + 1;
        HopCount = descriptor & 0x3F;
        var length = HashSize * HopCount;
        if (HashSize > 3 || length > ProtocolLimits.ContactPathSize || path.Length < length || path.Length > ProtocolLimits.ContactPathSize)
            throw new ArgumentException("Invalid contact route descriptor or incomplete path.", nameof(path));
        Kind = HopCount == 0 ? PrivateRouteKind.Direct : PrivateRouteKind.Path;
        Path = path[..length].ToArray();
    }

    public byte Descriptor { get; }
    public ReadOnlyMemory<byte> Path { get; }
    public DateTimeOffset ObservedUtc { get; }
    public PrivateRouteKind Kind { get; }
    public int HashSize { get; }
    public int HopCount { get; }
}

public sealed record BeginPrivateDeliveryCycle(Guid NodeId, Guid MessageId, Guid SessionId,
    PrivateRetryPolicy Policy, PrivateRouteSnapshot InitialRoute, DateTimeOffset StartedUtc);

public sealed record PrivateDeliveryCycleSnapshot(Guid NodeId, Guid MessageId, Guid? SessionId,
    ReadOnlyMemory<byte> ContactPublicKey, PrivateRetryPolicy Policy, PrivateRouteKind InitialRouteKind,
    int PlannedAttemptCount, int PreparedAttemptCount, PrivateDeliveryState State, DateTimeOffset StartedUtc,
    DateTimeOffset? ConfirmedUtc, string? ErrorCode, PrivateDeliveryPhase? CurrentPhase);

/// <summary>Idempotent CAS preparation. ExpectedAttemptNumber is zero for the existing first Prepared attempt.</summary>
public sealed record PreparePrivateAttempt(Guid PreparationId, Guid NodeId, Guid MessageId, Guid SessionId,
    int ExpectedAttemptNumber, PrivateRouteSnapshot Route, DateTimeOffset PreparedPcTime, string PcTimeZoneId);

public sealed record PrivateWireMessageSnapshot(Guid Id, Guid MessageId, int Ordinal, PrivateDeliveryPhase Phase, uint Timestamp);

public sealed record PrivateAttemptCapture(Guid PreparationId, Guid AttemptId, PrivateWireMessageSnapshot WireMessage,
    byte WireAttempt, PrivateRouteSnapshot Route, DateTimeOffset PreparedPcUtc, int PcUtcOffsetMinutes, string PcTimeZoneId);

public sealed record PreparedPrivateAttempt(PreparedOutgoingMessage Message, PrivateAttemptCapture Capture);
