using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Runtime.Transactions;

internal sealed class SinglePacketTransaction<TPacket> : ICommandTransaction
    where TPacket : CompanionPacket
{
    private readonly TaskCompletionSource<CompanionPacket> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<CompanionPacket> Completion => _completion.Task;

    public bool TryAccept(CompanionPacket packet)
    {
        if (packet is not TPacket && packet is not ErrorPacket)
            return false;

        return _completion.TrySetResult(packet);
    }

    public void Fail(Exception exception) => _completion.TrySetException(exception);
}
