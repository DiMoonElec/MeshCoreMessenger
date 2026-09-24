using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Protocol.Packets;
using MeshCoreSharp.Protocol.Parsing;
using MeshCoreSharp.Runtime;
using MeshCoreSharp.Transport;

namespace MeshCoreSharp;

public sealed class MeshCoreClient : IAsyncDisposable
{
    private readonly IMeshCoreTransport _transport;
    private readonly MeshCoreClientOptions _options;
    private readonly CompanionPacketDecoder _decoder = new();
    private readonly PacketRouter _router = new();
    private readonly CommandDispatcher _dispatcher;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private MeshCoreConnectionState _state = MeshCoreConnectionState.Disconnected;
    private bool _started;
    private bool _disposed;

    public MeshCoreClient(
        IMeshCoreTransport transport,
        MeshCoreClientOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new MeshCoreClientOptions();

        if (string.IsNullOrWhiteSpace(_options.ApplicationName))
            throw new ArgumentException("Application name must not be empty.", nameof(options));
        if (_options.CommandTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Command timeout must be positive.");

        _dispatcher = new CommandDispatcher(_transport, _router);
    }

    public MeshCoreConnectionState State => _state;
    public bool IsConnected => _state == MeshCoreConnectionState.Connected && _transport.IsConnected;
    public bool IsStarted => _started;
    public SelfInfo? SelfInfo { get; private set; }

    public event EventHandler<CompanionPacketEventArgs>? PacketReceived;
    public event EventHandler<CompanionPacketEventArgs>? PushPacketReceived;
    public event EventHandler<CompanionPacketEventArgs>? UnhandledPacketReceived;
    public event EventHandler<MeshCoreConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<MeshCoreClientErrorEventArgs>? BackgroundError;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == MeshCoreConnectionState.Connected)
                return;

            SetState(MeshCoreConnectionState.Connecting);
            try
            {
                await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                _receiveCts = new CancellationTokenSource();
                _receiveTask = ReceiveLoopAsync(_receiveCts.Token);
                _started = false;
                SelfInfo = null;
                SetState(MeshCoreConnectionState.Connected);
            }
            catch
            {
                SetState(MeshCoreConnectionState.Faulted);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<SelfInfo> StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var packet = await _dispatcher.SendAsync<SelfInfoPacket>(
            CommandType.AppStart,
            CompanionCommands.AppStart(_options.ApplicationName, _options.ApplicationProtocolVersion),
            nameof(StartAsync),
            _options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);

        SelfInfo = packet.Info;
        _started = true;
        return packet.Info;
    }

    public async Task<DateTimeOffset> GetDeviceTimeAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();
        var packet = await _dispatcher.SendAsync<CurrentTimePacket>(
            CommandType.GetDeviceTime,
            CompanionCommands.GetDeviceTime(),
            nameof(GetDeviceTimeAsync),
            _options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);

        return packet.Value;
    }

    public async Task SetDeviceTimeAsync(
        DateTimeOffset value,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        _ = await _dispatcher.SendAsync<OkPacket>(
            CommandType.SetDeviceTime,
            CompanionCommands.SetDeviceTime(value),
            nameof(SetDeviceTimeAsync),
            _options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();
        var packet = await _dispatcher.SendAsync<DeviceInfoPacket>(
            CommandType.DeviceQuery,
            CompanionCommands.DeviceQuery(_options.ApplicationProtocolVersion),
            nameof(GetDeviceInfoAsync),
            _options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);

        return packet.Info;
    }

    public async Task<BatteryAndStorageInfo> GetBatteryAndStorageAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        var packet = await _dispatcher.SendAsync<BatteryAndStoragePacket>(
            CommandType.GetBatteryAndStorage,
            CompanionCommands.GetBatteryAndStorage(),
            nameof(GetBatteryAndStorageAsync),
            _options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);

        return packet.Info;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == MeshCoreConnectionState.Disconnected)
                return;

            SetState(MeshCoreConnectionState.Disconnecting);
            _started = false;
            SelfInfo = null;

            if (_receiveCts is not null)
                await _receiveCts.CancelAsync().ConfigureAwait(false);

            await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);

            if (_receiveTask is not null)
            {
                try { await _receiveTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }

            _receiveCts?.Dispose();
            _receiveCts = null;
            _receiveTask = null;
            SetState(MeshCoreConnectionState.Disconnected);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            {
                CompanionPacket packet;
                try
                {
                    packet = _decoder.Decode(frame);
                }
                catch (Exception ex) when (ex is MeshCoreProtocolException or ArgumentException)
                {
                    RaiseSafely(BackgroundError, new MeshCoreClientErrorEventArgs(ex));
                    continue;
                }

                var matched = _router.Route(packet);
                var args = new CompanionPacketEventArgs(packet);

                RaiseSafely(PacketReceived, args);

                if (packet.IsPush)
                    RaiseSafely(PushPacketReceived, args);
                else if (!matched)
                    RaiseSafely(UnhandledPacketReceived, args);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                var exception = new MeshCoreTransportException("MeshCore transport closed the receive stream.");
                _router.FailCurrent(exception);
                SetState(MeshCoreConnectionState.Faulted);
                RaiseSafely(BackgroundError, new MeshCoreClientErrorEventArgs(exception));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _router.FailCurrent(ex);
            if (!cancellationToken.IsCancellationRequested)
            {
                SetState(MeshCoreConnectionState.Faulted);
                RaiseSafely(BackgroundError, new MeshCoreClientErrorEventArgs(ex));
            }
        }
    }

    private void EnsureConnected()
    {
        ThrowIfDisposed();
        if (!IsConnected)
            throw new InvalidOperationException("MeshCore client is not connected.");
    }

    private void EnsureReady()
    {
        EnsureConnected();
        if (!_started)
            throw new InvalidOperationException("APP_START has not completed. Call StartAsync() first.");
    }

    private void SetState(MeshCoreConnectionState state)
    {
        var previous = _state;
        if (previous == state)
            return;

        _state = state;
        RaiseSafely(
            ConnectionStateChanged,
            new MeshCoreConnectionStateChangedEventArgs(previous, state));
    }

    private void RaiseSafely<TEventArgs>(EventHandler<TEventArgs>? handler, TEventArgs args)
        where TEventArgs : EventArgs
    {
        if (handler is null)
            return;

        foreach (EventHandler<TEventArgs> subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch
            {
                // A consumer event handler must never terminate the protocol receive loop.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        try
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            _disposed = true;
            _dispatcher.Dispose();
            await _transport.DisposeAsync().ConfigureAwait(false);
            _lifecycleGate.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
