using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Runtime.Transactions;

internal interface ICommandTransaction
{
    Task<CompanionPacket> Completion { get; }
    bool TryAccept(CompanionPacket packet);
    bool TryFailMalformed(PacketType type, Exception exception) => false;
    void Fail(Exception exception);
}
