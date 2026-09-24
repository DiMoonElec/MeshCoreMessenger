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
    private readonly ClientEventQueue _events;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private MessagePump? _messagePump;
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
        ValidateContactsTimeout(_options.ContactsInactivityTimeout, nameof(MeshCoreClientOptions.ContactsInactivityTimeout));
        ValidateContactsTimeout(_options.ContactsAbsoluteTimeout, nameof(MeshCoreClientOptions.ContactsAbsoluteTimeout));

        _dispatcher = new CommandDispatcher(_transport, _router);
        _events = new ClientEventQueue();
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
    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == MeshCoreConnectionState.Connected)
                return;

            // An explicitly requested connection after a fault starts a fresh pump/session.
            if (_receiveCts is not null)
            {
                await _receiveCts.CancelAsync().ConfigureAwait(false);
                if (_messagePump is not null)
                    await _messagePump.DisposeAsync().ConfigureAwait(false);
                if (_receiveTask is not null)
                    await _receiveTask.ConfigureAwait(false);
                _receiveCts.Dispose();
                _messagePump = null;
                _receiveTask = null;
                _receiveCts = null;
            }

            SetState(MeshCoreConnectionState.Connecting);
            try
            {
                await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                _receiveCts = new CancellationTokenSource();
                _started = false;
                SelfInfo = null;
                _messagePump = _options.AutoReceiveMessages
                    ? new MessagePump(
                        ct => _dispatcher.SyncNextMessageAsync(_options.CommandTimeout, ct),
                        ex => RaiseSafely(BackgroundError, new MeshCoreClientErrorEventArgs(ex)),
                        _receiveCts.Token)
                    : null;
                _receiveTask = ReceiveLoopAsync(_receiveCts.Token, _messagePump);
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
        var messagePump = _messagePump;

        var packet = await _dispatcher.SendAsync<SelfInfoPacket>(
            CommandType.AppStart,
            CompanionCommands.AppStart(_options.ApplicationName, _options.ApplicationProtocolVersion),
            nameof(StartAsync),
            _options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);

        SelfInfo = packet.Info;
        _started = true;
        messagePump?.Start();
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

    /// <summary>Reads contacts until CONTACT_END. Unrelated push packets remain available through events.</summary>
    public Task<IReadOnlyList<Contact>> GetContactsAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();
        return _dispatcher.SendContactsAsync(
            CompanionCommands.GetContacts(),
            _options.ContactsInactivityTimeout,
            _options.ContactsAbsoluteTimeout,
            cancellationToken);
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

            _messagePump?.Stop();
            _router.FailCurrent(new MeshCoreTransportException("MeshCore client disconnected."));

            if (_receiveCts is not null)
                await _receiveCts.CancelAsync().ConfigureAwait(false);

            if (_messagePump is not null)
                await _messagePump.DisposeAsync().ConfigureAwait(false);
            _messagePump = null;

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

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken, MessagePump? messagePump)
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
                    if (!frame.IsEmpty)
                        _router.RouteMalformed((PacketType)frame.Span[0], ex);
                    RaiseSafely(BackgroundError, new MeshCoreClientErrorEventArgs(ex));
                    continue;
                }

                var matched = _router.Route(packet);
                var args = new CompanionPacketEventArgs(packet);

                RaiseSafely(PacketReceived, args);

                if (packet is ReceivedMessagePacket received)
                    RaiseSafely(MessageReceived, new MessageReceivedEventArgs(received.Message));

                if (packet.Type == PacketType.MessagesWaiting)
                    messagePump?.Notify();

                if (packet.IsPush)
                    RaiseSafely(PushPacketReceived, args);
                else if (!matched)
                    RaiseSafely(UnhandledPacketReceived, args);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                var exception = new MeshCoreTransportException("MeshCore transport closed the receive stream.");
                messagePump?.Stop();
                _router.FailCurrent(exception);
                _started = false;
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
            messagePump?.Stop();
            _router.FailCurrent(ex);
            _started = false;
            if (!cancellationToken.IsCancellationRequested)
            {
                SetState(MeshCoreConnectionState.Faulted);
                RaiseSafely(BackgroundError, new MeshCoreClientErrorEventArgs(ex));
            }
        }
        finally
        {
            messagePump?.Stop();
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

        _events.Post(() =>
        {
            foreach (EventHandler<TEventArgs> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(this, args);
                }
                catch
                {
                    // A consumer event handler must never terminate event delivery.
                }
            }
        });
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
            _events.Dispose();
            await _transport.DisposeAsync().ConfigureAwait(false);
            _lifecycleGate.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void ValidateContactsTimeout(TimeSpan timeout, string name)
    {
        if (timeout < TimeSpan.FromMilliseconds(1) || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(name, "Contacts timeout must be between 1 ms and 4294967294 ms.");
    }
}
