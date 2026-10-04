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
    private AckTracker? _ackTracker;
    private long _lastMessageTimestamp;
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
        if (_options.MinimumAckTimeout <= TimeSpan.Zero || _options.MaximumAckTimeout < _options.MinimumAckTimeout ||
            _options.MaximumAckTimeout.TotalMilliseconds > uint.MaxValue - 1 || _options.AckTimeoutMargin < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid ACK timeout bounds or margin.");

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
    public event EventHandler<AdvertisementReceivedEventArgs>? AdvertisementReceived;

    /// <summary>
    /// Waits until all synchronous event callbacks queued before this barrier have returned.
    /// </summary>
    /// <remarks>
    /// This does not drain messages from the Companion, wait for application persistence, or
    /// await work that an event callback starts after returning. Events queued after this call's
    /// barrier do not delay it. Call this after stopping RX and before disposing the client;
    /// do not synchronously wait for it from inside a client event callback.
    /// </remarks>
    public async Task FlushEventsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _events.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

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
                _ackTracker = new AckTracker(_options);
                _started = false;
                SelfInfo = null;
                _messagePump = new MessagePump(
                    ct => _dispatcher.SyncNextMessageAsync(_options.CommandTimeout, ct),
                    ex => RaiseSafely(BackgroundError, new MeshCoreClientErrorEventArgs(ex)),
                    _receiveCts.Token);
                _receiveTask = ReceiveLoopAsync(_receiveCts.Token, _messagePump, _ackTracker);
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
        messagePump?.Start(_options.AutoReceiveMessages);
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

    /// <summary>Reads one current contact by its full public key, without enumerating the directory.</summary>
    public async Task<Contact> GetContactAsync(ReadOnlyMemory<byte> publicKey, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        var command = CompanionCommands.GetContact(publicKey.Span);
        var key = command.AsMemory(1); // The command owns the captured key before the first await.
        var packet = await _dispatcher.SendAsync<ContactPacket>(CommandType.GetContactByKey, command,
            nameof(GetContactAsync), _options.CommandTimeout, cancellationToken,
            acceptsPacket: packet => packet.Contact.PublicKey.Span.SequenceEqual(key.Span)).ConfigureAwait(false);
        return packet.Contact;
    }

    /// <summary>Creates or replaces a Companion contact and waits for local OK/ERROR.</summary>
    /// <remarks>Call <see cref="GetContactsAsync"/> afterwards when the application needs a refreshed snapshot.</remarks>
    public async Task AddOrUpdateContactAsync(ContactConfiguration contact,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        await _dispatcher.SendAsync<OkPacket>(CommandType.AddOrUpdateContact,
            CompanionCommands.AddOrUpdateContact(contact), nameof(AddOrUpdateContactAsync),
            _options.CommandTimeout, cancellationToken, type => type == PacketType.Ok).ConfigureAwait(false);
    }

    /// <summary>Creates or replaces a Companion contact from a contact received in a list or NEW_ADVERT.</summary>
    public Task AddOrUpdateContactAsync(Contact contact, CancellationToken cancellationToken = default) =>
        AddOrUpdateContactAsync(ContactConfiguration.FromContact(contact), cancellationToken);

    /// <summary>Stores contact details from a NEW_ADVERT notification.</summary>
    /// <exception cref="ArgumentException">The notification contains only a key and no discovered contact details.</exception>
    public Task AddOrUpdateContactAsync(AdvertisementInfo advertisement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        if (advertisement.DiscoveredContact is null)
            throw new ArgumentException("Only a NEW_ADVERT notification contains enough data to add a contact.", nameof(advertisement));
        return AddOrUpdateContactAsync(advertisement.DiscoveredContact, cancellationToken);
    }

    /// <summary>Clears the stored route for a contact. Future sends use flood until a route is learned again.</summary>
    public async Task ResetPathAsync(ReadOnlyMemory<byte> publicKey, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        await _dispatcher.SendAsync<OkPacket>(CommandType.ResetPath,
            CompanionCommands.ResetPath(publicKey.Span), nameof(ResetPathAsync),
            _options.CommandTimeout, cancellationToken, type => type == PacketType.Ok).ConfigureAwait(false);
    }

    /// <summary>Removes a Companion contact using its complete 32-byte public key.</summary>
    public async Task RemoveContactAsync(ReadOnlyMemory<byte> publicKey,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        await _dispatcher.SendAsync<OkPacket>(CommandType.RemoveContact,
            CompanionCommands.RemoveContact(publicKey.Span), nameof(RemoveContactAsync),
            _options.CommandTimeout, cancellationToken, type => type == PacketType.Ok).ConfigureAwait(false);
    }

    /// <summary>Removes a Companion contact using a decoded contact model.</summary>
    public Task RemoveContactAsync(Contact contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        return RemoveContactAsync(contact.PublicKey, cancellationToken);
    }

    /// <summary>Reads queued incoming messages until the Companion reports NO_MORE_MESSAGES.</summary>
    /// <remarks>
    /// Messages are published through <see cref="MessageReceived"/>. Concurrent calls share one drain.
    /// Canceling one caller does not cancel a drain used by other callers. After a response timeout,
    /// reconnect before retrying because a late response cannot be correlated safely.
    /// </remarks>
    public Task DrainMessagesAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();
        return _messagePump!.DrainAsync(cancellationToken);
    }

    /// <summary>Reads one local channel slot, including an empty slot. Invalid indices produce a firmware error.</summary>
    public async Task<ChannelInfo> GetChannelAsync(byte index, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        var packet = await _dispatcher.SendAsync<ChannelInfoPacket>(CommandType.GetChannel,
            CompanionCommands.GetChannel(index), nameof(GetChannelAsync), _options.CommandTimeout, cancellationToken,
            type => type == PacketType.ChannelInfo, packet => packet.Info.Index == index).ConfigureAwait(false);
        return packet.Info;
    }

    /// <summary>Reads all channel slots, including empty slots, using the capacity reported by DEVICE_INFO.</summary>
    /// <remarks>This is a sequence of local reads, not an atomic snapshot. Older firmware without a capacity is unsupported.</remarks>
    public async Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        var device = await GetDeviceInfoAsync(cancellationToken).ConfigureAwait(false);
        if (device.MaxChannels is not { } count)
            throw new NotSupportedException("Firmware does not report channel capacity. Read individual slots with GetChannelAsync().");
        var channels = new List<ChannelInfo>(count);
        for (var index = 0; index < count; index++)
            channels.Add(await GetChannelAsync((byte)index, cancellationToken).ConfigureAwait(false));
        return channels.AsReadOnly();
    }

    /// <summary>Creates or replaces a local channel slot using an exact 16-byte shared secret.</summary>
    /// <remarks>The secret is copied into the command and is not logged by the library.</remarks>
    public async Task SetChannelAsync(byte index, string name, ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        ArgumentException.ThrowIfNullOrEmpty(name);
        await _dispatcher.SendAsync<OkPacket>(CommandType.SetChannel,
            CompanionCommands.SetChannel(index, name, secret.Span), nameof(SetChannelAsync),
            _options.CommandTimeout, cancellationToken, type => type == PacketType.Ok).ConfigureAwait(false);
    }

    /// <summary>Creates or replaces a public hashtag channel using the standard name-derived secret.</summary>
    /// <remarks>The exact name is hashed verbatim and must include the leading '#'. Hashtag channels are not private.</remarks>
    public Task SetHashtagChannelAsync(byte index, string channelName,
        CancellationToken cancellationToken = default)
    {
        var secret = ChannelSecrets.DeriveHashtag(channelName);
        return SetChannelAsync(index, channelName, secret, cancellationToken);
    }

    /// <summary>Clears a local channel slot by writing an empty name and an all-zero secret.</summary>
    public async Task ClearChannelAsync(byte index, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        await _dispatcher.SendAsync<OkPacket>(CommandType.SetChannel,
            CompanionCommands.ClearChannel(index), nameof(ClearChannelAsync),
            _options.CommandTimeout, cancellationToken, type => type == PacketType.Ok).ConfigureAwait(false);
    }

    /// <summary>Reads battery, uptime, error flags and outbound queue length (firmware protocol v8+).</summary>
    public async Task<CoreStats> GetCoreStatsAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();
        var packet = await _dispatcher.SendAsync<CoreStatsPacket>(CommandType.GetStats,
            CompanionCommands.GetStats(StatsType.Core), nameof(GetCoreStatsAsync), _options.CommandTimeout,
            cancellationToken, type => type == PacketType.Stats).ConfigureAwait(false);
        return packet.Info;
    }

    /// <summary>Reads local radio measurements and airtime counters (firmware protocol v8+).</summary>
    public async Task<RadioStats> GetRadioStatsAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();
        var packet = await _dispatcher.SendAsync<RadioStatsPacket>(CommandType.GetStats,
            CompanionCommands.GetStats(StatsType.Radio), nameof(GetRadioStatsAsync), _options.CommandTimeout,
            cancellationToken, type => type == PacketType.Stats).ConfigureAwait(false);
        return packet.Info;
    }

    /// <summary>Reads local radio packet counters (firmware protocol v8+).</summary>
    public async Task<PacketStats> GetPacketStatsAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();
        var packet = await _dispatcher.SendAsync<PacketStatsPacket>(CommandType.GetStats,
            CompanionCommands.GetStats(StatsType.Packets), nameof(GetPacketStatsAsync), _options.CommandTimeout,
            cancellationToken, type => type == PacketType.Stats).ConfigureAwait(false);
        return packet.Info;
    }

    /// <summary>Sends one self advertisement using the node's existing name/location policy. Completes on local OK.</summary>
    /// <remarks>No periodic advertising or retries. Cancellation after writing cannot retract the radio transmission.</remarks>
    public async Task SendAdvertisementAsync(AdvertisementMode mode = AdvertisementMode.ZeroHop,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        var command = CompanionCommands.SendAdvertisement(mode);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _receiveCts!.Token);
        await _dispatcher.SendAsync<OkPacket>(CommandType.SendSelfAdvertisement, command,
            nameof(SendAdvertisementAsync), _options.CommandTimeout, linked.Token,
            type => type == PacketType.Ok).ConfigureAwait(false);
    }

    /// <summary>Sends plain text once. Returns on Companion acceptance; await Delivery for the separate ACK result.</summary>
    /// <remarks>The cancellation token also governs Delivery. Cancellation/timeout never retracts or retries a radio send.</remarks>
    public Task<TextMessageSendResult> SendTextAsync(
        Contact recipient, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        return SendTextAsync(recipient.PublicKey, text, cancellationToken);
    }

    /// <summary>Sends plain text once using a full 32-byte recipient public key.</summary>
    /// <remarks>The cancellation token also governs Delivery. Cancellation/timeout never retracts or retries a radio send.</remarks>
    public async Task<TextMessageSendResult> SendTextAsync(
        ReadOnlyMemory<byte> recipientPublicKey, string text, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        var tracker = _ackTracker!;
        var timestamp = NextMessageTimestamp();
        var command = CompanionCommands.SendText(recipientPublicKey.Span, text, timestamp);
        var pending = await tracker.BeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sent = await _dispatcher.SendAsync<MessageSentPacket>(CommandType.SendTextMessage, command,
                nameof(SendTextAsync), _options.CommandTimeout, pending.Token,
                type => type == PacketType.MessageSent, onAccepted: packet => pending.Bind(packet.Info)).ConfigureAwait(false);
            pending.ReleaseSendGate();
            return new TextMessageSendResult(timestamp, sent.Info, pending.WaitForDeliveryAsync());
        }
        catch
        {
            pending.Dispose();
            throw;
        }
    }

    /// <summary>Sends plain channel text once and waits for local OK. This does not confirm remote delivery.</summary>
    public async Task<ChannelMessageSendResult> SendChannelTextAsync(
        byte channelIndex, string text, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        return await SendChannelTextAsync(channelIndex, text, NextMessageTimestamp(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends once with the supplied wire timestamp, for durable application-owned retries.</summary>
    public async Task<ChannelMessageSendResult> SendChannelTextAsync(
        byte channelIndex, string text, uint timestamp, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        while (true)
        {
            var previous = Interlocked.Read(ref _lastMessageTimestamp);
            if (previous >= timestamp || Interlocked.CompareExchange(ref _lastMessageTimestamp, timestamp, previous) == previous) break;
        }
        var command = CompanionCommands.SendChannelText(channelIndex, SelfInfo!.Name, text, timestamp);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _receiveCts!.Token);
        await _dispatcher.SendAsync<OkPacket>(CommandType.SendChannelTextMessage, command,
            nameof(SendChannelTextAsync), _options.CommandTimeout, linked.Token,
            type => type == PacketType.Ok).ConfigureAwait(false);
        return new ChannelMessageSendResult(channelIndex, timestamp);
    }

    private uint NextMessageTimestamp()
    {
        // ACK hashes omit the recipient. Distinct timestamps prevent identical text sent to
        // different contacts in the same second from sharing a tag; no automatic retry is used.
        while (true)
        {
            var previous = Interlocked.Read(ref _lastMessageTimestamp);
            var next = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), previous + 1);
            if (next > uint.MaxValue) throw new InvalidOperationException("Message timestamp exceeds the protocol range.");
            if (Interlocked.CompareExchange(ref _lastMessageTimestamp, next, previous) == previous) return (uint)next;
        }
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
            _ackTracker?.Stop();
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

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken, MessagePump? messagePump, AckTracker ackTracker)
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

                if (packet is AckPacket ack) ackTracker.Handle(ack.Info);
                var matched = _router.Route(packet);
                var args = new CompanionPacketEventArgs(packet);

                RaiseSafely(PacketReceived, args);

                if (packet is ReceivedMessagePacket received)
                    RaiseSafely(MessageReceived, new MessageReceivedEventArgs(received.Message));

                if (packet is AdvertisementPacket advert)
                    RaiseSafely(AdvertisementReceived, new AdvertisementReceivedEventArgs(advert.Info));

                if (_options.AutoReceiveMessages && packet.Type == PacketType.MessagesWaiting)
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
            ackTracker.Stop();
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
