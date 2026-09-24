using System.Collections.Concurrent;
using System.Threading.Channels;
using MeshCoreSharp.Transport.Serial;

internal sealed class FakeSerialConnection : ISerialConnection
{
    private readonly BlockingCollection<object> _input = new();
    private byte[]? _chunk;
    private int _offset;
    private int _writers;
    public Action? OnOpen { get; set; }
    public Action<byte[]>? OnWrite { get; set; }
    public Channel<byte[]> Written { get; } = Channel.CreateUnbounded<byte[]>();
    public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int DisposeCount;
    public int MaxConcurrentWrites;

    public void Open()
    {
        OnOpen?.Invoke();
        Opened.TrySetResult();
    }

    public void Emit(byte[] bytes) => _input.Add(bytes);
    public void FailRead(Exception error) => _input.Add(error);

    public int Read(byte[] buffer, int offset, int count)
    {
        if (_chunk is null)
        {
            if (!_input.TryTake(out var value, 20)) throw new TimeoutException();
            if (value is Exception error) throw error;
            _chunk = (byte[])value;
            _offset = 0;
        }
        var length = Math.Min(count, _chunk.Length - _offset);
        _chunk.AsSpan(_offset, length).CopyTo(buffer.AsSpan(offset));
        _offset += length;
        if (_offset == _chunk.Length) _chunk = null;
        return length;
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        var writers = Interlocked.Increment(ref _writers);
        if (writers > MaxConcurrentWrites) MaxConcurrentWrites = writers;
        try
        {
            if (DisposeCount != 0) throw new ObjectDisposedException(nameof(FakeSerialConnection));
            var bytes = buffer.AsSpan(offset, count).ToArray();
            OnWrite?.Invoke(bytes);
            Written.Writer.TryWrite(bytes);
        }
        finally { Interlocked.Decrement(ref _writers); }
    }

    public void Dispose() => Interlocked.Increment(ref DisposeCount);
}
