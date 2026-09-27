using MeshCoreSharp;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Application;

public interface IMeshCoreClientFactory
{
    ICompanionClient Create(Domain.ConnectionProfile profile);
}

public interface ICompanionClient : IAsyncDisposable
{
    MeshCoreConnectionState State { get; }
    bool IsConnected { get; }
    bool IsStarted { get; }

    event EventHandler<MeshCoreConnectionStateChangedEventArgs>? ConnectionStateChanged;
    event EventHandler<MeshCoreClientErrorEventArgs>? BackgroundError;
    event EventHandler<CompanionPacketEventArgs>? PacketReceived;
    event EventHandler<CompanionPacketEventArgs>? PushPacketReceived;
    event EventHandler<CompanionPacketEventArgs>? UnhandledPacketReceived;
    event EventHandler<MessageReceivedEventArgs>? MessageReceived;
    event EventHandler<AdvertisementReceivedEventArgs>? AdvertisementReceived;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task<SelfInfo> StartAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Contact>> GetContactsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task FlushEventsAsync(CancellationToken cancellationToken = default);
}
