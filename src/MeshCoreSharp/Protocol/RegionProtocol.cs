using MeshCoreSharp.Models;

namespace MeshCoreSharp.Protocol;

internal static class RegionProtocol
{
    internal const byte DiscoverRequest = 0x80;
    internal const byte DiscoverResponse = 0x90;
    internal const byte ControlTypeMask = 0xF0;
    internal const byte NodeTypeMask = 0x0F;
    internal const byte RepeaterFilter = 1 << (int)AdvertisementType.Repeater;
    internal const byte RegionsAnonymousSubtype = 1;
    internal const int DiscoveryPrefixSize = 8;
    internal const int DiscoveryResponseHeaderSize = 6;
}
