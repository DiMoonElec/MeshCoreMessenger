namespace MeshCoreSharp.Models;

public enum FloodScopeMode { InheritDefault, ExplicitRegion, ForceUnscoped }

/// <summary>Outgoing flood policy. Unknown observations are not a selectable policy.</summary>
public sealed class FloodScopeSelection
{
    private FloodScopeSelection(FloodScopeMode mode, RegionKey? key = null) { Mode = mode; Key = key; }
    public FloodScopeMode Mode { get; }
    public RegionKey? Key { get; }
    public static FloodScopeSelection InheritDefault { get; } = new(FloodScopeMode.InheritDefault);
    public static FloodScopeSelection ForceUnscoped { get; } = new(FloodScopeMode.ForceUnscoped);
    public static FloodScopeSelection ExplicitRegion(RegionKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.IsEmpty) throw new ArgumentException("An explicit key cannot be all-zero (firmware default fallback).", nameof(key));
        return new(FloodScopeMode.ExplicitRegion, key);
    }
}
