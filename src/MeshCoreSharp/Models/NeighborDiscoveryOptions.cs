namespace MeshCoreSharp.Models;

public sealed record NeighborDiscoveryOptions
{
    public TimeSpan CollectionWindow { get; init; } = TimeSpan.FromSeconds(3);
    public int MaximumNeighbors { get; init; } = 256;
    public uint? Since { get; init; }
}

/// <summary>A bounded zero-hop observation window. Empty results do not prove absence of repeaters.</summary>
public sealed record NeighborDiscoveryResult(uint Tag, IReadOnlyList<DiscoveredNeighbor> Neighbors, bool CapacityReached);
