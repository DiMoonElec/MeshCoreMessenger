using MeshCoreSharp;
using MeshCoreSharp.Models;

internal static class RegionModelTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("REGION1 public derivation vectors and exact names", Derivation),
        ("REGION1 UTF-8 endpoint boundaries and invalid names", Names),
        ("REGION1 immutable keys and default observation states", Keys),
    ];
    private static void Check(bool condition) { if (!condition) throw new Exception("Region model regression"); }
    private static void Reject(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new Exception("Expected invalid region rejection");
    }
    private static Task Derivation()
    {
        Check(Convert.ToHexString(RegionKeys.DerivePublic("foo").ToArray()) == "5BCADC2B60BE87D52F213E0411B5438E");
        Check(RegionKeys.DerivePublic("foo").Equals(RegionKeys.DerivePublic("#foo")));
        Check(!RegionKeys.DerivePublic("Foo").Equals(RegionKeys.DerivePublic("foo")));
        Check(!RegionKeys.DerivePublic("é").Equals(RegionKeys.DerivePublic("e\u0301")));
        return Task.CompletedTask;
    }
    private static Task Names()
    {
        foreach (var name in new[] { "", "#", "##foo", "*", "$secret", "a,b", "a\0b", "a b", "\ud800" })
            Reject(() => RegionKeys.DerivePublic(name));
        _ = RegionDefinition.Public(new string('я', 15));
        _ = RegionDefinition.Public("#" + new string('a', 29));
        Reject(() => RegionDefinition.Public("#" + new string('a', 30)));
        Reject(() => RegionDefinition.Public(new string('я', 16)));
        _ = RegionKeys.DerivePublic(new string('a', 30));
        Reject(() => RegionKeys.DerivePublic(new string('a', 31)));
        return Task.CompletedTask;
    }
    private static Task Keys()
    {
        Reject(() => new RegionKey(new byte[15]));
        Reject(() => new RegionKey(new byte[17]));
        Reject(() => FloodScopeSelection.ExplicitRegion(new RegionKey(new byte[16])));
        var bytes = Enumerable.Repeat((byte)7, 16).ToArray();
        var key = new RegionKey(bytes);
        bytes[0] = 9;
        var output = key.ToArray(); output[1] = 8;
        Check(key.ToArray().All(b => b == 7));
        Check(key.Equals(new RegionKey(Enumerable.Repeat((byte)7, 16).ToArray())));
        Check(NodeFloodScope.Unknown.State != NodeFloodScope.KnownEmpty.State);
        var stored = NodeFloodScope.FromPreferences("$custom", key);
        Check(stored.Name == "$custom" && stored.Key!.Equals(key));
        Check(RegionDefinition.Custom("$custom", key).Kind == RegionKeyKind.Custom);
        Check(FloodScopeSelection.InheritDefault.Mode != FloodScopeSelection.ForceUnscoped.Mode);
        return Task.CompletedTask;
    }
}
