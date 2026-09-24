using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Runtime.Transactions;

internal interface ICommandTransaction
{
    Task<CompanionPacket> Completion { get; }
    bool TryAccept(CompanionPacket packet);
    void Fail(Exception exception);
}
