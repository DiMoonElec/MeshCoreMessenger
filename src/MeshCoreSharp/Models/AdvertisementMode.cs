namespace MeshCoreSharp.Models;

public enum AdvertisementMode : byte
{
    /// <summary>Advertise to immediate neighbours without forwarding.</summary>
    ZeroHop = 0,
    /// <summary>Allow forwarding within the node's configured flood scope.</summary>
    Flood = 1,
}
