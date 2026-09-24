using System.IO.Ports;
using System.Runtime.CompilerServices;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Transport.Framing;

namespace MeshCoreSharp.Transport.Serial;

/// <summary>MeshCore Companion over a serial port, using 8N1 and no flow control.</summary>
public sealed class SerialMeshCoreTransport : IMeshCoreTransport
{
    private readonly SerialMeshCoreTransportOptions _options;
    private readonly Func<SerialMeshCoreTransportOptions, ISerialConnection> _createPort;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private volatile SerialTransportSession? _session;
    private volatile bool _disposed;

    public SerialMeshCoreTransport(string portName, int baudRate = 115200)
        : this(new SerialMeshCoreTransportOptions { PortName = portName, BaudRate = baudRate }) { }

    public SerialMeshCoreTransport(SerialMeshCoreTransportOptions options)
        : this(options, static value => new SerialPortConnection(value)) { }

    internal SerialMeshCoreTransport(SerialMeshCoreTransportOptions options,
        Func<SerialMeshCoreTransportOptions, ISerialConnection> createPort)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(createPort);
        if (string.IsNullOrWhiteSpace(options.PortName))
            throw new ArgumentException("Serial port name must not be empty.", nameof(options));
        if (options.BaudRate <= 0 || options.ReadTimeout <= 0 || options.WriteTimeout <= 0 || options.ReceiveBufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Baud rate, I/O timeouts and receive buffer size must be positive.");
        if (options.DecoderSafetyLimit is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), "Decoder safety limit must be between 1 and 65535.");
        if (options.OpenDelay < TimeSpan.Zero || options.OpenDelay.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Open delay is outside the supported timer range.");

        _options = options;
        _createPort = createPort;
    }

    public bool IsConnected => _session is { Token.IsCancellationRequested: false };

    /// <summary>Enumerates OS serial port names without opening any device.</summary>
    public static string[] GetPortNames() => SerialPort.GetPortNames().Order(StringComparer.Ordinal).ToArray();

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsConnected) return;
            await CleanupAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            ISerialConnection? port = null;
            try
            {
                port = _createPort(_options);
                // Open is synchronous in System.IO.Ports. Await it before disposing on cancellation.
                await Task.Run(port.Open, cancellationToken).ConfigureAwait(false);
                await Task.Delay(_options.OpenDelay, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                _session = new SerialTransportSession(port, _options);
                port = null; // Ownership belongs to the session.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new MeshCoreTransportException($"Failed to open MeshCore serial port '{_options.PortName}'.", ex);
            }
            finally
            {
                port?.Dispose();
            }
        }
        finally { _lifecycleGate.Release(); }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> companionFrame, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = _session ?? throw new InvalidOperationException("Serial transport is not connected.");
        var framed = StreamFrameEncoder.Encode(companionFrame.Span, StreamFrameEncoder.AppToCompanionMarker);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token);
        try
        {
            await session.WriteGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                // Finite WriteTimeout bounds driver I/O. Do not interrupt a successful write halfway
                // through a frame merely because the caller cancelled; observe cancellation afterwards.
                await Task.Run(() => session.Port.Write(framed, 0, framed.Length), linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
            }
            finally { session.WriteGate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            throw new MeshCoreTransportException("Serial connection closed while sending a frame.", ex);
        }
        catch (Exception ex)
        {
            var error = new MeshCoreTransportException("Failed to write a MeshCore serial frame.", ex);
            // A failed/partial write loses framing alignment. Require an explicit new connection.
            session.Stop(error);
            throw error;
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = _session ?? throw new InvalidOperationException("Serial transport is not connected.");
        await foreach (var frame in session.Frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return frame;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await CleanupAsync().ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
    }

    private async Task CleanupAsync()
    {
        var session = _session;
        _session = null;
        if (session is null) return;
        session.Stop();
        try { await session.Completion.ConfigureAwait(false); }
        catch (Exception ex) { throw new MeshCoreTransportException("Failed to close the MeshCore serial port.", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await CleanupAsync().ConfigureAwait(false);
        }
        finally { _lifecycleGate.Release(); }
        // Keep the managed gate alive for concurrent lifecycle callers to observe the disposed state.
    }
}
