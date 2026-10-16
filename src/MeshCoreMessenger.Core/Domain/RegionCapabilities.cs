namespace MeshCoreMessenger.Core.Domain;

/// <summary>Session-specific feature evidence. A protocol version can rule a feature out;
/// it does not prove every feature is implemented by that firmware build.</summary>
public sealed record RegionCapabilities(RegionSupport TemporaryScope, RegionSupport ForceUnscoped,
    RegionSupport DefaultReadWrite, RegionSupport NeighborDiscovery, RegionSupport AnonymousRegions)
{
    public static RegionCapabilities Unknown { get; } = new(RegionSupport.Unknown, RegionSupport.Unknown,
        RegionSupport.Unknown, RegionSupport.Unknown, RegionSupport.Unknown);
    public static RegionCapabilities FromProtocolVersion(byte version) => Unknown with
    {
        TemporaryScope = version < 8 ? RegionSupport.Unsupported : RegionSupport.Unknown,
        ForceUnscoped = version < 12 ? RegionSupport.Unsupported : RegionSupport.Unknown,
        NeighborDiscovery = version < 8 ? RegionSupport.Unsupported : RegionSupport.Unknown,
    };
}
