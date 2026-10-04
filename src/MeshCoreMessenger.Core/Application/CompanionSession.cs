using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Application;

/// <summary>Owns one Companion client and one persisted connection attempt.</summary>
public sealed class CompanionSession : IAsyncDisposable
{
    private readonly ICompanionClient _client;
    private readonly INodeStore _nodes;
    private readonly ISessionStore _sessions;
    private readonly IDurableSessionCompletion _sessionCompletions;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Channel<CompanionSessionEvent> _events = Channel.CreateUnbounded<CompanionSessionEvent>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private volatile CompanionSessionState _state = CompanionSessionState.Created;
    private int _closed;
    private SessionCommandScope? _commands;
    private int _disposed;

    internal CompanionSession(
        Guid sessionId,
        long generation,
        Guid profileId,
        ICompanionClient client,
        INodeStore nodes,
        ISessionStore sessions,
        IDurableSessionCompletion sessionCompletions,
        TimeProvider timeProvider)
    {
        SessionId = sessionId;
        Generation = generation;
        ProfileId = profileId;
        _client = client;
        _nodes = nodes;
        _sessions = sessions;
        _sessionCompletions = sessionCompletions;
        _timeProvider = timeProvider;

        _client.ConnectionStateChanged += OnConnectionStateChanged;
        _client.BackgroundError += OnBackgroundError;
        _client.PacketReceived += OnPacketReceived;
        _client.PushPacketReceived += OnPushPacketReceived;
        _client.UnhandledPacketReceived += OnUnhandledPacketReceived;
        _client.MessageReceived += OnMessageReceived;
        _client.AdvertisementReceived += OnAdvertisementReceived;
    }

    public Guid SessionId { get; }
    public long Generation { get; }
    public Guid ProfileId { get; }
    public Guid? LocalNodeId { get; private set; }
    public LocalNodeIdentity? LocalNode { get; private set; }
    public CompanionSessionState State => _state;
    public ChannelReader<CompanionSessionEvent> Events => _events.Reader;

    internal event EventHandler<CompanionSessionLifecycleEventArgs>? LifecycleChanged;

    internal SessionCommandScope OpenCommands(SessionCommandGateway gateway)
    {
        EnsureIdentified();
        if (_commands is not null) throw new InvalidOperationException("Command admission has already been opened for this session.");
        return _commands = gateway.Open(new(LocalNodeId!.Value, SessionId, Generation, LocalNode!.Name), _client);
    }

    internal Task<IReadOnlyList<Contact>> GetContactsAsync(CancellationToken cancellationToken = default)
    {
        EnsureIdentified();
        return _client.GetContactsAsync(cancellationToken);
    }

    internal Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        EnsureIdentified();
        return _client.GetChannelsAsync(cancellationToken);
    }

    internal Task DrainMessagesAsync(CancellationToken cancellationToken = default)
    {
        EnsureIdentified();
        return _client.DrainMessagesAsync(cancellationToken);
    }

    /// <summary>Places a marker after all library callbacks, for the one application event consumer.</summary>
    internal async Task FlushEventsForConsumerAsync(CancellationToken cancellationToken = default)
    {
        EnsureIdentified();
        await _client.FlushEventsAsync(cancellationToken).ConfigureAwait(false);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _events.Writer.WriteAsync(NewEvent(CompanionSessionEventKind.EventBarrier) with
        {
            BarrierCompletion = completion,
        }, cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CompanionSessionStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        // A persisted attempt must always be closed, including when the caller supplies an
        // already-canceled token. Cancellation is observed inside the owned lifecycle section.
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_state != CompanionSessionState.Created)
            {
                throw new InvalidOperationException("A Companion session can only be started once.");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetState(CompanionSessionState.Connecting);
                await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                SetState(CompanionSessionState.Identifying);
                var self = await _client.StartAsync(cancellationToken).ConfigureAwait(false);
                var publicKey = self.PublicKey.ToArray();
                if (publicKey.Length != 32)
                {
                    throw new InvalidDataException("Companion returned a local node key that is not 32 bytes long.");
                }

                LocalNode = new LocalNodeIdentity(publicKey, self.Name);
                var node = await _nodes.FindOrCreateAsync(
                    publicKey,
                    self.Name,
                    _timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
                LocalNodeId = node.Id;
                await _sessions.BindNodeAsync(SessionId, node.Id, cancellationToken).ConfigureAwait(false);

                SetState(CompanionSessionState.Identified);
                return new CompanionSessionStartResult(
                    SessionId,
                    Generation,
                    node.Id,
                    LocalNode);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CloseAfterStartFailureAsync(
                    "Start canceled",
                    CompanionSessionState.Stopped).ConfigureAwait(false);
                throw;
            }
            catch
            {
                await CloseAfterStartFailureAsync(
                    "Start failed",
                    CompanionSessionState.Failed).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task CloseAfterStartFailureAsync(string reason, CompanionSessionState finalState)
    {
        try
        {
            await CloseCoreAsync(reason, finalState, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The original connect/start exception remains the primary failure. CloseCoreAsync
            // already attempted every cleanup step and persisted the end marker where possible.
        }
    }

    public async Task StopAsync(
        string reason = "Stopped",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                return;
            }

            await CloseCoreAsync(reason.Trim(), CompanionSessionState.Stopped, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await StopAsync("Disposed", CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Dispose();
        }
    }

    private async Task CloseCoreAsync(
        string reason,
        CompanionSessionState finalState,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _commands?.Close();
        SetState(CompanionSessionState.Stopping);
        Exception? cleanupError = null;
        try
        {
            await _client.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupError = exception;
        }

        try
        {
            if (_commands is not null) await _commands.DrainAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupError = exception;
        }

        try
        {
            await _client.FlushEventsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupError ??= exception;
        }

        _client.ConnectionStateChanged -= OnConnectionStateChanged;
        _client.BackgroundError -= OnBackgroundError;
        _client.PacketReceived -= OnPacketReceived;
        _client.PushPacketReceived -= OnPushPacketReceived;
        _client.UnhandledPacketReceived -= OnUnhandledPacketReceived;
        _client.MessageReceived -= OnMessageReceived;
        _client.AdvertisementReceived -= OnAdvertisementReceived;
        try
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupError ??= exception;
        }

        try
        {
            await _sessionCompletions.EndAsync(
                SessionId,
                _timeProvider.GetUtcNow(),
                reason,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Durable session completion must dominate transport cleanup failures so
            // the supervisor cannot classify a database problem as a transient reconnect.
            cleanupError = exception;
        }

        SetState(finalState);
        _events.Writer.TryComplete();
        if (cleanupError is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupError).Throw();
        }
    }

    private void OnConnectionStateChanged(object? sender, MeshCoreConnectionStateChangedEventArgs args)
    {
        _events.Writer.TryWrite(NewEvent(CompanionSessionEventKind.ConnectionStateChanged) with
        {
            PreviousConnectionState = args.PreviousState,
            CurrentConnectionState = args.CurrentState,
        });
        RaiseLifecycle(new CompanionSessionLifecycleEventArgs
        {
            SessionId = SessionId,
            Generation = Generation,
            ConnectionState = args.CurrentState,
        });
    }

    private void OnBackgroundError(object? sender, MeshCoreClientErrorEventArgs args)
    {
        _events.Writer.TryWrite(NewEvent(CompanionSessionEventKind.BackgroundError) with
        {
            ErrorType = args.Exception.GetType().FullName,
            ErrorMessage = args.Exception.Message,
        });
        RaiseLifecycle(new CompanionSessionLifecycleEventArgs
        {
            SessionId = SessionId,
            Generation = Generation,
            Error = args.Exception,
        });
    }

    private void OnPacketReceived(object? sender, CompanionPacketEventArgs args) =>
        CopyPacket(CompanionSessionEventKind.PacketReceived, args);

    private void OnPushPacketReceived(object? sender, CompanionPacketEventArgs args) =>
        CopyPacket(CompanionSessionEventKind.PushPacketReceived, args);

    private void OnUnhandledPacketReceived(object? sender, CompanionPacketEventArgs args) =>
        CopyPacket(CompanionSessionEventKind.UnhandledPacketReceived, args);

    private void CopyPacket(CompanionSessionEventKind kind, CompanionPacketEventArgs args) =>
        _events.Writer.TryWrite(NewEvent(kind) with
        {
            RawPacketType = args.Packet.RawType,
            RawFrame = args.Packet.RawFrame.ToArray(),
            Acknowledgement = (args.Packet as MeshCoreSharp.Protocol.Packets.AckPacket)?.Info,
        });

    private void OnMessageReceived(object? sender, MessageReceivedEventArgs args) =>
        _events.Writer.TryWrite(NewEvent(CompanionSessionEventKind.MessageReceived) with
        {
            Message = CopyMessage(args.Message),
        });

    private void OnAdvertisementReceived(object? sender, AdvertisementReceivedEventArgs args) =>
        _events.Writer.TryWrite(NewEvent(CompanionSessionEventKind.AdvertisementReceived) with
        {
            Advertisement = CopyAdvertisement(args.Advertisement),
        });

    private CompanionSessionEvent NewEvent(CompanionSessionEventKind kind) => new()
    {
        SessionId = SessionId,
        Generation = Generation,
        OccurredUtc = _timeProvider.GetUtcNow(),
        Kind = kind,
    };

    private void SetState(CompanionSessionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        RaiseLifecycle(new CompanionSessionLifecycleEventArgs
        {
            SessionId = SessionId,
            Generation = Generation,
            SessionState = state,
        });
    }

    private void RaiseLifecycle(CompanionSessionLifecycleEventArgs args)
    {
        foreach (EventHandler<CompanionSessionLifecycleEventArgs> handler in
                 LifecycleChanged?.GetInvocationList().Cast<EventHandler<CompanionSessionLifecycleEventArgs>>() ?? [])
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // Lifecycle observers must not affect session cleanup or the application event queue.
            }
        }
    }

    private static ReceivedMessage CopyMessage(ReceivedMessage message) => message switch
    {
        ContactMessage contact => contact with
        {
            ContactPublicKeyPrefix = contact.ContactPublicKeyPrefix.ToArray(),
            SenderPrefix = contact.SenderPrefix.ToArray(),
        },
        ChannelDataMessage data => data with { Data = data.Data.ToArray() },
        ChannelMessage channel => channel with { },
        _ => message,
    };

    private static AdvertisementInfo CopyAdvertisement(AdvertisementInfo advertisement) => new(
        advertisement.PublicKey.ToArray(),
        advertisement.DiscoveredContact is null
            ? null
            : advertisement.DiscoveredContact with
            {
                PublicKey = advertisement.DiscoveredContact.PublicKey.ToArray(),
                OutPath = advertisement.DiscoveredContact.OutPath.ToArray(),
            });

    private void EnsureIdentified()
    {
        if (_state != CompanionSessionState.Identified || LocalNodeId is null)
        {
            throw new InvalidOperationException("Companion session has not completed node identification.");
        }
    }
}
