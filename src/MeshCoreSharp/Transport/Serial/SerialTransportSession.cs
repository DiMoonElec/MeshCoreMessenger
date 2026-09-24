using System.Threading.Channels;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Transport.Framing;

namespace MeshCoreSharp.Transport.Serial;

internal sealed class SerialTransportSession
{
    private readonly CancellationTokenSource _stop = new();
    private readonly StreamFrameDecoder _decoder;
    private readonly int _bufferSize;
    private Exception? _error;
    private int _stopped;

    public SerialTransportSession(ISerialConnection port, SerialMeshCoreTransportOptions options)
    {
        Port = port;
        Token = _stop.Token;
        _bufferSize = options.ReceiveBufferSize;
        _decoder = new StreamFrameDecoder(StreamFrameEncoder.CompanionToAppMarker, options.DecoderSafetyLimit);
        var reader = Task.Factory.StartNew(ReadLoop, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Completion = CloseAfterReadAsync(reader);
    }

    public ISerialConnection Port { get; }
    public CancellationToken Token { get; }
    public Task Completion { get; }
    public SemaphoreSlim WriteGate { get; } = new(1, 1);
    public Channel<ReadOnlyMemory<byte>> Frames { get; } = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });

    public void Stop(Exception? error = null)
    {
        if (error is not null)
            Interlocked.CompareExchange(ref _error, error, null);
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
            _stop.Cancel();
    }

    private void ReadLoop()
    {
        var buffer = new byte[_bufferSize];
        try
        {
            while (!Token.IsCancellationRequested)
            {
                int read;
                try { read = Port.Read(buffer, 0, buffer.Length); }
                catch (TimeoutException) { continue; } // An idle UART is healthy.

                if (read == 0)
                    throw new IOException("Serial port closed the receive stream.");
                if (Token.IsCancellationRequested)
                    break;
                foreach (var frame in _decoder.Push(buffer.AsSpan(0, read)))
                    Frames.Writer.TryWrite(frame);
            }
        }
        catch (Exception ex) when (!Token.IsCancellationRequested)
        {
            Stop(new MeshCoreTransportException("MeshCore serial receive loop failed.", ex));
        }
        catch (Exception) when (Token.IsCancellationRequested) { }
        finally
        {
            Stop();
            Frames.Writer.TryComplete(_error);
        }
    }

    private async Task CloseAfterReadAsync(Task reader)
    {
        try
        {
            await reader.ConfigureAwait(false);
            // Never dispose the port under an active write. Queued writers observe Token cancellation.
            await WriteGate.WaitAsync().ConfigureAwait(false);
            try { Port.Dispose(); }
            finally { WriteGate.Release(); }
        }
        finally
        {
            _stop.Dispose();
        }
        // WriteGate stays undisposed so already queued/cancelled senders can unwind safely.
    }
}
