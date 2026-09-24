using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp;

public sealed class CompanionPacketEventArgs : EventArgs
{
    public CompanionPacketEventArgs(CompanionPacket packet) => Packet = packet;
    public CompanionPacket Packet { get; }
}
