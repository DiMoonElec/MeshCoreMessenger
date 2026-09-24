using System.Threading.Channels;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Transport;

internal sealed class TestTransport : IMeshCoreTransport
{
    private Channel<ReadOnlyMemory<byte>> _received = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    public Channel<byte[]> Sent { get; } = Channel.CreateUnbounded<byte[]>();
    public Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? OnSend { get; set; }
    public bool IsConnected { get; private set; }
    public bool AutoReplyToMessageSync { get; set; } = true;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_received.Reader.Completion.IsCompleted)
            _received = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Close();
        return Task.CompletedTask;
    }

    public void Close()
    {
        IsConnected = false;
        _received.Writer.TryComplete();
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> companionFrame, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OnSend is not null) return OnSend(companionFrame, cancellationToken);
        if ((CommandType)companionFrame.Span[0] == CommandType.AppStart)
        {
            var self = new byte[58];
            self[0] = (byte)PacketType.SelfInfo;
            Emit(self);
        }
        else if (AutoReplyToMessageSync && (CommandType)companionFrame.Span[0] == CommandType.SyncNextMessage)
            Emit([(byte)PacketType.NoMoreMessages]);
        else
            Sent.Writer.TryWrite(companionFrame.ToArray());
        return ValueTask.CompletedTask;
    }

    public void Emit(byte[] frame) => _received.Writer.TryWrite(frame);

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _received.Reader.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync() { Close(); return ValueTask.CompletedTask; }
}
