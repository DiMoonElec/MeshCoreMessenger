using MeshCoreSharp.Protocol.Packets;
using MeshCoreSharp.Runtime.Transactions;

namespace MeshCoreSharp.Runtime;

internal sealed class PacketRouter
{
    private readonly object _sync = new();
    private ICommandTransaction? _currentCommand;

    public void Register(ICommandTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        lock (_sync)
        {
            if (_currentCommand is not null)
                throw new InvalidOperationException("A companion command transaction is already registered.");

            _currentCommand = transaction;
        }
    }

    public void Unregister(ICommandTransaction transaction)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_currentCommand, transaction))
                _currentCommand = null;
        }
    }

    public bool Route(CompanionPacket packet)
    {
        ICommandTransaction? transaction;
        lock (_sync)
            transaction = _currentCommand;

        return transaction?.TryAccept(packet) ?? false;
    }

    public void FailCurrent(Exception exception)
    {
        ICommandTransaction? transaction;
        lock (_sync)
        {
            transaction = _currentCommand;
            _currentCommand = null;
        }

        transaction?.Fail(exception);
    }
}
