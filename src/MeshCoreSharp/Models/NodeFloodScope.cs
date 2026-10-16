namespace MeshCoreSharp.Models;

public enum NodeFloodScopeState { Unknown, KnownEmpty, Configured }

/// <summary>Observed default preferences. The received name/key pair is authoritative.</summary>
public sealed class NodeFloodScope
{
    private NodeFloodScope(NodeFloodScopeState state, string? name = null, RegionKey? key = null)
    { State = state; Name = name; Key = key; }
    public NodeFloodScopeState State { get; }
    public string? Name { get; }
    public RegionKey? Key { get; }
    public static NodeFloodScope Unknown { get; } = new(NodeFloodScopeState.Unknown);
    public static NodeFloodScope KnownEmpty { get; } = new(NodeFloodScopeState.KnownEmpty);

    /// <summary>Preserves even a custom or empty stored key without deriving a replacement.</summary>
    public static NodeFloodScope FromPreferences(string name, RegionKey key)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(key);
        if (name.Contains('\0')) throw new ArgumentException("A preference name cannot contain NUL.", nameof(name));
        if (new System.Text.UTF8Encoding(false, true).GetByteCount(name) > Protocol.ProtocolLimits.MaxRegionNameUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(name));
        return new(NodeFloodScopeState.Configured, name, key);
    }
}
