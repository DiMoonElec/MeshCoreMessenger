using MeshCoreSharp.Models;

namespace MeshCoreSharp;

public sealed class AdvertisementReceivedEventArgs(AdvertisementInfo advertisement) : EventArgs
{
    public AdvertisementInfo Advertisement { get; } = advertisement;
}
