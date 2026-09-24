namespace MeshCoreSharp.Models;

/// <summary>A local channel slot, including its 128-bit shared secret.</summary>
/// <param name="Secret">Sensitive key material. Do not include it in logs.</param>
public sealed record ChannelInfo(byte Index, string Name, ReadOnlyMemory<byte> Secret)
{
    /// <summary>Whether the slot has an empty name.</summary>
    public bool IsEmpty => Name.Length == 0;

    public override string ToString() => $"Channel {Index}: {Name}";
}
