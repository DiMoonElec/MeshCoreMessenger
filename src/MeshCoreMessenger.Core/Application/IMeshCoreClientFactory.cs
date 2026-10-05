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
    Task<Contact> GetContactAsync(ReadOnlyMemory<byte> publicKey, CancellationToken cancellationToken = default) =>
        Task.FromException<Contact>(new NotSupportedException("Single contact reading is not implemented by this adapter."));
    Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken = default);
    Task DrainMessagesAsync(CancellationToken cancellationToken = default);
    Task<TextMessageSendResult> SendTextAsync(ReadOnlyMemory<byte> recipientPublicKey, string text, CancellationToken cancellationToken = default) =>
        Task.FromException<TextMessageSendResult>(new NotSupportedException("Text sending is not implemented by this adapter."));
    Task<TextMessageSendResult> SendTextAsync(ReadOnlyMemory<byte> recipientPublicKey, string text, uint timestamp,
        byte attempt, CancellationToken cancellationToken = default) =>
        Task.FromException<TextMessageSendResult>(new NotSupportedException("Captured text sending is not implemented by this adapter."));
    /// <summary>Configured library wait duration for diagnostic deadlines; the library owns the actual monotonic timer.</summary>
    TimeSpan? GetAcknowledgementWaitDuration(uint suggestedMilliseconds) => null;
    Task<ChannelMessageSendResult> SendChannelTextAsync(byte slot, string text, uint timestamp, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task<ChannelMessageSendResult> SendChannelTextAsync(byte slot, string text, CancellationToken cancellationToken = default) =>
        Task.FromException<ChannelMessageSendResult>(new NotSupportedException("Channel sending is not implemented by this adapter."));
    Task AddOrUpdateContactAsync(ContactConfiguration contact, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Contact mutation is not implemented by this adapter."));
    Task ResetPathAsync(ReadOnlyMemory<byte> publicKey, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Route reset is not implemented by this adapter."));
    Task RemoveContactAsync(ReadOnlyMemory<byte> publicKey, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Contact mutation is not implemented by this adapter."));
    Task SetChannelAsync(byte slot, string name, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Channel mutation is not implemented by this adapter."));
    Task ClearChannelAsync(byte slot, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Channel mutation is not implemented by this adapter."));
    Task SendAdvertisementAsync(AdvertisementMode mode, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Advertisement sending is not implemented by this adapter."));
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task FlushEventsAsync(CancellationToken cancellationToken = default);
}
