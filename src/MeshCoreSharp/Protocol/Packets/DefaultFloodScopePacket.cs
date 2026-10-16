using MeshCoreSharp.Models;

namespace MeshCoreSharp.Protocol.Packets;

public sealed class DefaultFloodScopePacket : CompanionPacket
{
    internal DefaultFloodScopePacket(ReadOnlyMemory<byte> frame, NodeFloodScope scope)
        : base((byte)PacketType.DefaultFloodScope, frame) => Scope = scope;
    public NodeFloodScope Scope { get; }
}
