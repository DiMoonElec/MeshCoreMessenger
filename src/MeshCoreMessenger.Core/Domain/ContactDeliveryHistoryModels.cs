namespace MeshCoreMessenger.Core.Domain;

public enum DeliveryRouteProvenance { ConfiguredBeforeSend, ContactReadbackAfterAcknowledgement }
public sealed record DeliveryRouteReadbackRequest(Guid EvidenceId, Guid NodeId, Guid SessionId, ReadOnlyMemory<byte> ContactPublicKey);
public sealed record LearnedDeliveryRoute(DeliveryRouteReadbackRequest Target, PrivateRouteSnapshot Route);
public sealed record ContactDeliveryCursor(Guid NodeId, ReadOnlyMemory<byte> ContactPublicKey, DateTimeOffset AckReceivedUtc, Guid DeliveryId);
public sealed record ContactDeliveryCandidate(int AttemptNumber, Guid? AttemptId, int WireMessageOrdinal,
    uint WireTimestamp, byte WireAttempt, PrivateDeliveryPhase Phase, DateTimeOffset? SentUtc,
    int? PcUtcOffsetMinutes, string? PcTimeZoneId, PrivateRouteSnapshot? ConfiguredRoute, bool? ModeReportedByMsgSent)
{
    public DeliveryRouteProvenance? RouteProvenance => ConfiguredRoute is null ? null : DeliveryRouteProvenance.ConfiguredBeforeSend;
}
public sealed record ContactDeliveryEvidence(Guid Id, uint AckTag, DateTimeOffset AckReceivedUtc,
    uint? RoundTripMilliseconds, DeliveryAttribution Attribution, PrivateRouteSnapshot? LearnedRoute,
    IReadOnlyList<ContactDeliveryCandidate> Candidates)
{
    public DeliveryRouteProvenance? LearnedRouteProvenance => LearnedRoute is null ? null : DeliveryRouteProvenance.ContactReadbackAfterAcknowledgement;
}
public sealed record ContactDeliveryRecord(Guid Id, Guid NodeId, ReadOnlyMemory<byte> ContactPublicKey,
    Guid? MessageId, Guid? SessionId, DateTimeOffset FirstAckReceivedUtc, int PcUtcOffsetMinutes,
    string PcTimeZoneId, bool WasLate, DeliveryAttribution Attribution, IReadOnlyList<ContactDeliveryEvidence> Evidence);
public sealed record ContactDeliveryPage(IReadOnlyList<ContactDeliveryRecord> Items, ContactDeliveryCursor? NextCursor);
