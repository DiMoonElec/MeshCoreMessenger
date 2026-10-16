using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Packets;
using MeshCoreSharp.Runtime.Transactions;

namespace MeshCoreSharp.Runtime;

internal sealed class PacketRouter
{
    private readonly object _sync = new();
    private ICommandTransaction? _currentCommand;
    private readonly HashSet<IRuntimePacketObserver> _observers = [];

    internal IDisposable Observe(IRuntimePacketObserver observer)
    {
        lock (_sync) _observers.Add(observer);
        return new Registration(this, observer);
    }

    private sealed class Registration(PacketRouter owner, IRuntimePacketObserver observer) : IDisposable
    {
        public void Dispose() { lock (owner._sync) owner._observers.Remove(observer); }
    }

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
        IRuntimePacketObserver[] observers;
        lock (_sync)
        {
            transaction = _currentCommand;
            observers = _observers.ToArray();
        }

        // MSG_SENT's immediate acceptance hook binds deferred correlation before the
        // next logical frame can be routed to any observer.
        var matched = transaction?.TryAccept(packet) ?? false;
        foreach (var observer in observers) matched |= observer.Observe(packet);
        return matched;
    }

    public void FailCurrent(Exception exception)
    {
        ICommandTransaction? transaction;
        IRuntimePacketObserver[] observers;
        lock (_sync)
        {
            transaction = _currentCommand;
            _currentCommand = null;
            observers = _observers.ToArray();
            _observers.Clear();
        }

        transaction?.Fail(exception);
        foreach (var observer in observers) observer.Fail(exception);
    }

    public void RouteMalformed(PacketType type, Exception exception)
    {
        ICommandTransaction? transaction;
        lock (_sync)
            transaction = _currentCommand;

        transaction?.TryFailMalformed(type, exception);
    }
}
