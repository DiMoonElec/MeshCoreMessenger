using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Runtime;

internal sealed class NeighborDiscoveryCollector : IRuntimePacketObserver, IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DiscoveredNeighbor> _neighbors = [];
    private readonly TaskCompletionSource<NeighborDiscoveryResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly uint _tag;
    private readonly int _maximum;
    private readonly ITimer _timer;
    private bool _capacityReached;
    private bool _stopped;

    internal NeighborDiscoveryCollector(uint tag, int maximum, TimeSpan window, TimeProvider? clock = null)
    {
        _tag = tag; _maximum = maximum;
        _timer = (clock ?? TimeProvider.System).CreateTimer(_ => Complete(), null, window, Timeout.InfiniteTimeSpan);
    }
    internal Task<NeighborDiscoveryResult> Completion => _completion.Task;
    public bool Observe(CompanionPacket packet)
    {
        if (packet is not ControlDataPacket { Neighbor: { HasFullPublicKey: true, NodeType: AdvertisementType.Repeater } neighbor } || neighbor.Tag != _tag)
            return false;
        lock (_sync)
        {
            if (_stopped) return false;
            var identity = Convert.ToHexString(neighbor.GetKeyBytes());
            if (_neighbors.ContainsKey(identity)) return true;
            if (_neighbors.Count >= _maximum) _capacityReached = true;
            else _neighbors.Add(identity, neighbor);
            return true;
        }
    }
    private void Complete()
    {
        lock (_sync)
        {
            if (_stopped) return;
            _stopped = true;
            _completion.TrySetResult(new(_tag, Array.AsReadOnly(_neighbors.Values.ToArray()), _capacityReached));
        }
    }
    public void Fail(Exception error)
    {
        lock (_sync)
        {
            if (_stopped) return;
            _stopped = true; _completion.TrySetException(error);
        }
    }
    public void Dispose()
    {
        lock (_sync) _stopped = true;
        _timer.Dispose();
        if (_completion.Task.IsFaulted) _ = _completion.Task.Exception;
    }
}
