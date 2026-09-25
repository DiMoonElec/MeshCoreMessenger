using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Serial;
using MeshCoreSharp.Transport.Tcp;

namespace MeshCoreMessenger.Core.Application;

public sealed class MeshCoreClientFactory : IMeshCoreClientFactory
{
    public MeshCoreClient Create(ConnectionProfile profile)
    {
        var mapping = ConnectionProfileMapper.Map(profile);
        IMeshCoreTransport transport = mapping.Transport switch
        {
            ConnectionTransportKind.Tcp => new TcpMeshCoreTransport(mapping.TcpOptions!),
            ConnectionTransportKind.Serial => new SerialMeshCoreTransport(mapping.SerialOptions!),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), "Unknown connection transport."),
        };
        return new MeshCoreClient(transport, mapping.ClientOptions);
    }
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
