using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Application;

/// <summary>Serializes clear admission against the entire send/ACK lifetime, including before Prepare.</summary>
public sealed class ConversationOperationGuard
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid, ConversationKind, string), int> _active = [];
    public bool IsBusy(Guid node, ConversationKind kind, ReadOnlyMemory<byte> identity)
    {
        lock (_gate) return _active.ContainsKey(Key(node, kind, identity));
    }
    public IDisposable BeginSend(Guid node, ConversationKind kind, ReadOnlyMemory<byte> identity) => Begin(node, kind, identity, false);
    public IDisposable BeginClear(Guid node, ConversationKind kind, ReadOnlyMemory<byte> identity) => Begin(node, kind, identity, true);
    private IDisposable Begin(Guid node, ConversationKind kind, ReadOnlyMemory<byte> identity, bool clear)
    {
        var key = Key(node, kind, identity);
        lock (_gate)
        {
            _active.TryGetValue(key, out var count);
            if (count < 0 || clear && count != 0) throw new InvalidOperationException("В переписке выполняется отправка или очистка. Дождитесь завершения.");
            _active[key] = clear ? -1 : count + 1;
        }
        return new Release(() =>
        {
            lock (_gate)
            {
                if (_active[key] <= 1) _active.Remove(key);
                else _active[key]--;
            }
        });
    }
    private static (Guid, ConversationKind, string) Key(Guid node, ConversationKind kind, ReadOnlyMemory<byte> identity) =>
        (node, kind, Convert.ToHexString(identity.Span));
    private sealed class Release(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
