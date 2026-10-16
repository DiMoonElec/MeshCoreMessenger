namespace MeshCoreSharp.Models;

public enum RegionKeyKind { PublicDerived, Custom }

/// <summary>A validated region for outgoing configuration, without database identity.</summary>
public sealed class RegionDefinition
{
    private RegionDefinition(string name, RegionKey key, RegionKeyKind kind)
    { Name = name; Key = key; Kind = kind; }
    public string Name { get; }
    public RegionKey Key { get; }
    public RegionKeyKind Kind { get; }

    public static RegionDefinition Public(string name)
    {
        // Keep the caller's service prefix: SET_DEFAULT transmits the actual name.
        var key = RegionKeys.DerivePublic(name);
        RegionKeys.ValidateName(name);
        return new(name, key, RegionKeyKind.PublicDerived);
    }

    public static RegionDefinition Custom(string name, RegionKey key)
    {
        RegionKeys.ValidateName(name);
        if (name == "#" || name == "$" || name.Contains("##", StringComparison.Ordinal))
            throw new ArgumentException("Expected a nonempty region name.", nameof(name));
        _ = FloodScopeSelection.ExplicitRegion(key);
        return new(name, key, RegionKeyKind.Custom);
    }
}
