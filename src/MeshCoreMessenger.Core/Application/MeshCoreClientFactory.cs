using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp;
using MeshCoreSharp.Models;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Serial;
using MeshCoreSharp.Transport.Tcp;

namespace MeshCoreMessenger.Core.Application;

public sealed class MeshCoreClientFactory : IMeshCoreClientFactory
{
    public ICompanionClient Create(ConnectionProfile profile)
    {
        var mapping = ConnectionProfileMapper.Map(profile);
        IMeshCoreTransport transport = mapping.Transport switch
        {
            ConnectionTransportKind.Tcp => new TcpMeshCoreTransport(mapping.TcpOptions!),
            ConnectionTransportKind.Serial => new SerialMeshCoreTransport(mapping.SerialOptions!),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), "Unknown connection transport."),
        };
        return new MeshCoreClientAdapter(new MeshCoreClient(transport, mapping.ClientOptions));
    }
}

internal sealed class MeshCoreClientAdapter(MeshCoreClient client) : ICompanionClient
{
    public Task<Contact> GetContactAsync(ReadOnlyMemory<byte> publicKey, CancellationToken cancellationToken = default) =>
        client.GetContactAsync(publicKey, cancellationToken);
    public MeshCoreConnectionState State => client.State;
    public bool IsConnected => client.IsConnected;
    public bool IsStarted => client.IsStarted;

    public event EventHandler<MeshCoreConnectionStateChangedEventArgs>? ConnectionStateChanged
    {
        add => client.ConnectionStateChanged += value;
        remove => client.ConnectionStateChanged -= value;
    }

    public event EventHandler<MeshCoreClientErrorEventArgs>? BackgroundError
    {
        add => client.BackgroundError += value;
        remove => client.BackgroundError -= value;
    }

    public event EventHandler<CompanionPacketEventArgs>? PacketReceived
    {
        add => client.PacketReceived += value;
        remove => client.PacketReceived -= value;
    }

    public event EventHandler<CompanionPacketEventArgs>? PushPacketReceived
    {
        add => client.PushPacketReceived += value;
        remove => client.PushPacketReceived -= value;
    }

    public event EventHandler<CompanionPacketEventArgs>? UnhandledPacketReceived
    {
        add => client.UnhandledPacketReceived += value;
        remove => client.UnhandledPacketReceived -= value;
    }

    public event EventHandler<MessageReceivedEventArgs>? MessageReceived
    {
        add => client.MessageReceived += value;
        remove => client.MessageReceived -= value;
    }

    public event EventHandler<AdvertisementReceivedEventArgs>? AdvertisementReceived
    {
        add => client.AdvertisementReceived += value;
        remove => client.AdvertisementReceived -= value;
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        client.ConnectAsync(cancellationToken);

    public Task<MeshCoreSharp.Models.SelfInfo> StartAsync(CancellationToken cancellationToken = default) =>
        client.StartAsync(cancellationToken);

    public Task<IReadOnlyList<Contact>> GetContactsAsync(CancellationToken cancellationToken = default) =>
        client.GetContactsAsync(cancellationToken);

    public Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken = default) =>
        client.GetChannelsAsync(cancellationToken);

    public Task DrainMessagesAsync(CancellationToken cancellationToken = default) =>
        client.DrainMessagesAsync(cancellationToken);

    public Task<TextMessageSendResult> SendTextAsync(ReadOnlyMemory<byte> recipientPublicKey, string text, CancellationToken cancellationToken = default) =>
        client.SendTextAsync(recipientPublicKey, text, cancellationToken);
    public Task<TextMessageSendResult> SendTextAsync(ReadOnlyMemory<byte> recipientPublicKey, string text, uint timestamp,
        byte attempt, CancellationToken cancellationToken = default) =>
        client.SendTextAsync(recipientPublicKey, text, timestamp, attempt, cancellationToken);
    public Task<ChannelMessageSendResult> SendChannelTextAsync(byte slot, string text, uint timestamp, CancellationToken cancellationToken = default) =>
        client.SendChannelTextAsync(slot, text, timestamp, cancellationToken);
    public Task<ChannelMessageSendResult> SendChannelTextAsync(byte slot, string text, CancellationToken cancellationToken = default) =>
        client.SendChannelTextAsync(slot, text, cancellationToken);
    public Task AddOrUpdateContactAsync(ContactConfiguration contact, CancellationToken cancellationToken = default) =>
        client.AddOrUpdateContactAsync(contact, cancellationToken);
    public Task ResetPathAsync(ReadOnlyMemory<byte> publicKey, CancellationToken cancellationToken = default) =>
        client.ResetPathAsync(publicKey, cancellationToken);
    public Task RemoveContactAsync(ReadOnlyMemory<byte> publicKey, CancellationToken cancellationToken = default) =>
        client.RemoveContactAsync(publicKey, cancellationToken);
    public Task SetChannelAsync(byte slot, string name, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default) =>
        client.SetChannelAsync(slot, name, secret, cancellationToken);
    public Task ClearChannelAsync(byte slot, CancellationToken cancellationToken = default) =>
        client.ClearChannelAsync(slot, cancellationToken);
    public Task SendAdvertisementAsync(AdvertisementMode mode, CancellationToken cancellationToken = default) =>
        client.SendAdvertisementAsync(mode, cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        client.DisconnectAsync(cancellationToken);

    public Task FlushEventsAsync(CancellationToken cancellationToken = default) =>
        client.FlushEventsAsync(cancellationToken);

    public ValueTask DisposeAsync() => client.DisposeAsync();
}

internal static class ConnectionProfileMapper
{
    private static readonly TimeSpan DefaultMinimumAcknowledgementTimeout = TimeSpan.FromSeconds(1);

    public static ConnectionProfileMapping Map(ConnectionProfile profile)
    {
        ConnectionProfileValidator.Validate(profile);
        var acknowledgementTimeout = TimeSpan.FromMilliseconds(profile.AcknowledgementTimeoutMilliseconds);
        var clientOptions = new MeshCoreClientOptions
        {
            ApplicationName = "MeshCoreMessenger",
            CommandTimeout = TimeSpan.FromMilliseconds(profile.CommandTimeoutMilliseconds),
            AutoReceiveMessages = false,
            MinimumAckTimeout = acknowledgementTimeout < DefaultMinimumAcknowledgementTimeout
                ? acknowledgementTimeout
                : DefaultMinimumAcknowledgementTimeout,
            MaximumAckTimeout = acknowledgementTimeout,
        };

        return profile.Transport switch
        {
            ConnectionTransportKind.Tcp => new ConnectionProfileMapping(
                profile.Transport,
                new TcpMeshCoreTransportOptions
                {
                    Host = profile.TcpHost!,
                    Port = profile.TcpPort!.Value,
                },
                null,
                clientOptions),
            ConnectionTransportKind.Serial => new ConnectionProfileMapping(
                profile.Transport,
                null,
                new SerialMeshCoreTransportOptions
                {
                    PortName = profile.SerialPortName!,
                    BaudRate = profile.BaudRate!.Value,
                    DtrEnable = profile.DtrEnable,
                    RtsEnable = profile.RtsEnable,
                    OpenDelay = TimeSpan.FromMilliseconds(profile.OpenDelayMilliseconds),
                },
                clientOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), "Unknown connection transport."),
        };
    }
}

internal sealed record ConnectionProfileMapping(
    ConnectionTransportKind Transport,
    TcpMeshCoreTransportOptions? TcpOptions,
    SerialMeshCoreTransportOptions? SerialOptions,
    MeshCoreClientOptions ClientOptions);
