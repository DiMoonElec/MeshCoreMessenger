using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Runtime.Transactions;

internal sealed class SinglePacketTransaction<TPacket>(Func<PacketType, bool>? acceptsType = null,
    Func<TPacket, bool>? acceptsPacket = null, Action<TPacket>? onAccepted = null) : ICommandTransaction
    where TPacket : CompanionPacket
{
    private readonly TaskCompletionSource<CompanionPacket> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<CompanionPacket> Completion => _completion.Task;

    public bool TryAccept(CompanionPacket packet)
    {
        if (packet is not TPacket && packet is not ErrorPacket)
            return false;

        if (packet is TPacket typed && acceptsPacket?.Invoke(typed) == false)
            return false;

        if (_completion.Task.IsCompleted) return false;
        // Internal non-blocking hook: install the ACK waiter on RX before the next frame.
        try
        {
            if (packet is TPacket accepted) onAccepted?.Invoke(accepted);
            return _completion.TrySetResult(packet);
        }
        catch (Exception exception)
        {
            return _completion.TrySetException(exception);
        }
    }

    public void Fail(Exception exception) => _completion.TrySetException(exception);

    public bool TryFailMalformed(PacketType type, Exception exception)
    {
        if (acceptsType?.Invoke(type) != true)
            return false;
        Fail(exception);
        return true;
    }
}
