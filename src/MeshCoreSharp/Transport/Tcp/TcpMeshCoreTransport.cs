using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Transport.Framing;

namespace MeshCoreSharp.Transport.Tcp;

public sealed class TcpMeshCoreTransport : IMeshCoreTransport
{
    private readonly TcpMeshCoreTransportOptions _options;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private Channel<ReadOnlyMemory<byte>>? _frames;
    private StreamFrameDecoder? _decoder;
    private int _connected;
    private bool _disposed;

    public TcpMeshCoreTransport(string host, int port)
        : this(new TcpMeshCoreTransportOptions { Host = host, Port = port })
    {
    }

    public TcpMeshCoreTransport(TcpMeshCoreTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Host))
            throw new ArgumentException("TCP host must not be empty.", nameof(options));
        if (options.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options), "TCP port must be between 1 and 65535.");
        if (options.ReceiveBufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Receive buffer size must be positive.");

        _options = options;
    }

    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
                return;

            await CleanupConnectionAsync().ConfigureAwait(false);

            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(_options.Host, _options.Port, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                client.Dispose();
                throw new MeshCoreTransportException(
                    $"Failed to connect to MeshCore companion at {_options.Host}:{_options.Port}.", ex);
            }

            _client = client;
            _stream = client.GetStream();
            _decoder = new StreamFrameDecoder(
                StreamFrameEncoder.CompanionToAppMarker,
                _options.DecoderSafetyLimit);
            _frames = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false,
                });
            _receiveCts = new CancellationTokenSource();
            Volatile.Write(ref _connected, 1);
            _receiveTask = ReceiveLoopAsync(_receiveCts.Token);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CleanupConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> companionFrame,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var stream = _stream ?? throw new InvalidOperationException("TCP transport is not connected.");

        if (companionFrame.Length <= 0)
            throw new ArgumentOutOfRangeException(nameof(companionFrame), "Companion frame must not be empty.");

        var framed = StreamFrameEncoder.Encode(
            companionFrame.Span,
            StreamFrameEncoder.AppToCompanionMarker);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(framed, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new MeshCoreTransportException("Failed to write a MeshCore TCP frame.", ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var frames = _frames ?? throw new InvalidOperationException("TCP transport is not connected.");

        await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return frame;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("TCP transport is not connected.");
        var frames = _frames ?? throw new InvalidOperationException("TCP receive channel is not initialized.");
        var decoder = _decoder ?? throw new InvalidOperationException("TCP frame decoder is not initialized.");
        var buffer = new byte[_options.ReceiveBufferSize];

        Exception? completionError = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                foreach (var payload in decoder.Push(buffer.AsSpan(0, read)))
                    await frames.Writer.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal explicit disconnect.
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // The stream may throw ObjectDisposedException/IOException while an
            // explicit disconnect is tearing it down.
        }
        catch (Exception ex)
        {
            completionError = new MeshCoreTransportException("MeshCore TCP receive loop failed.", ex);
        }
        finally
        {
            Volatile.Write(ref _connected, 0);
            frames.Writer.TryComplete(completionError);
        }
    }

    private async Task CleanupConnectionAsync()
    {
        if (_receiveCts is not null)
        {
            try { await _receiveCts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }

        _stream?.Dispose();
        _client?.Dispose();

        if (_receiveTask is not null)
        {
            try { await _receiveTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (MeshCoreTransportException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        Volatile.Write(ref _connected, 0);
        _receiveCts?.Dispose();
        _receiveCts = null;
        _receiveTask = null;
        _stream = null;
        _client = null;
        _decoder = null;
        _frames = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            await CleanupConnectionAsync().ConfigureAwait(false);
            _disposed = true;
        }
        finally
        {
            _lifecycleGate.Release();
        }

        _writeGate.Dispose();
        _lifecycleGate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
