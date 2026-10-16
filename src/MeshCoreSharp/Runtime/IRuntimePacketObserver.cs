using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Runtime;

/// <summary>Internal bounded, nonblocking RX work. Never invokes application callbacks.</summary>
internal interface IRuntimePacketObserver
{
    bool Observe(CompanionPacket packet);
    void Fail(Exception error);
}
