using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Application;

/// <summary>Session-owned readback queue. Push processing never waits for a Companion command.</summary>
internal sealed class ContactRouteRefreshQueue(DirectoryService directories, CompanionSession session, Task ready, IDurableOutgoingWrites? outgoing = null) : IDisposable
{
    private readonly Dictionary<string, (byte[] Key, DeliveryRouteReadbackRequest? Evidence)> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    public Exception? LastError { get; private set; }

    public void Enqueue(ReadOnlyMemory<byte> publicKey)
    {
        if (publicKey.Length != 32) return;
        lock (_pending)
            if (_pending.TryAdd(Convert.ToHexString(publicKey.Span), (publicKey.ToArray(), null))) _signal.Release();
    }

    public void Enqueue(DeliveryRouteReadbackRequest request)
    {
        if (request.NodeId != session.LocalNodeId || request.SessionId != session.SessionId || request.ContactPublicKey.Length != 32) return;
        lock (_pending)
            if (_pending.TryAdd("Evidence:" + request.EvidenceId, (request.ContactPublicKey.ToArray(), request))) _signal.Release();
    }

    public async Task RunAsync(CancellationToken token)
    {
        try
        {
            try { await ready.WaitAsync(token).ConfigureAwait(false); }
            catch (Exception) when (!token.IsCancellationRequested) { return; } // Initial synchronization owns its failure.
            while (true)
            {
                await _signal.WaitAsync(token).ConfigureAwait(false);
                (byte[] Key, DeliveryRouteReadbackRequest? Evidence) work;
                lock (_pending)
                {
                    var entry = _pending.First();
                    work = entry.Value;
                    _pending.Remove(entry.Key);
                }
                try
                {
                    var route = await directories.RefreshContactRouteAsync(session, work.Key, token).ConfigureAwait(false);
                    if (work.Evidence is { } evidence && outgoing is not null)
                        await outgoing.SaveLearnedRouteAsync(new(evidence, route), token).ConfigureAwait(false);
                    LastError = null;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) { LastError = error; } // Read-only failure must not replay a send/reset.
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public void Dispose() => _signal.Dispose();
}
