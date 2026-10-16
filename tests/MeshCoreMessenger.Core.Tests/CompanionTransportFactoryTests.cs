using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Serial;
using MeshCoreSharp.Transport.Tcp;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class CompanionTransportFactoryTests
{
    [Theory]
    [InlineData(ConnectionTransportKind.Tcp)]
    [InlineData(ConnectionTransportKind.Serial)]
    [InlineData(ConnectionTransportKind.Ble)]
    public async Task MapsIdenticalClientOptionsAndCreatesFreshDisconnectedTransports(ConnectionTransportKind kind)
    {
        var profile = Profile(kind);
        var mapping = ConnectionProfileMapper.Map(profile);
        Assert.False(mapping.ClientOptions.AutoReceiveMessages);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), mapping.ClientOptions.CommandTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(5678), mapping.ClientOptions.MaximumAckTimeout);
        Assert.Equal("MeshCoreMessenger", mapping.ClientOptions.ApplicationName);
        var portable = new CompanionTransportFactory();
        if (kind == ConnectionTransportKind.Ble)
        {
            Assert.Equal(MeshCoreBleFailureReason.UnsupportedPlatform,
                Assert.Throws<MeshCoreBleTransportException>(() => new MeshCoreClientFactory().Create(profile)).Reason);
            return;
        }
        await using var first = portable.Create(profile);
        await using var second = portable.Create(profile);
        Assert.NotSame(first, second);
        Assert.False(first.IsConnected); Assert.False(second.IsConnected);
        if (kind == ConnectionTransportKind.Tcp) Assert.IsType<TcpMeshCoreTransport>(first);
        else Assert.IsType<SerialMeshCoreTransport>(first);
    }

    [Fact]
    public async Task InjectedBleProviderUsesExistingSessionIdentityAndExplicitDrain()
    {
        var root = Path.Combine(Path.GetTempPath(), "MeshCoreMessenger.BleFactory.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var storage = await LocalStorage.OpenAsync(new Paths(root), Token);
            var profile = Profile(ConnectionTransportKind.Ble);
            await storage.ConnectionProfiles.SaveAsync(profile, Token);
            var provider = new LocalhostProvider();
            var factory = new CompanionSessionFactory(new MeshCoreClientFactory(provider), storage.Nodes,
                storage.Sessions, new SessionCompletionTracker(storage.Sessions), TimeProvider.System);
            Guid? nodeId = null;
            for (var generation = 1; generation <= 2; generation++)
            {
                await using var server = new FakeCompanionServer(); server.Start();
                provider.Port = server.Port;
                await using var session = await factory.CreateAsync(profile, generation, Token);
                var start = await session.StartAsync(Token);
                if (nodeId is { } prior) Assert.Equal(prior, start.NodeId);
                nodeId = start.NodeId;
                Assert.Equal(2, (await session.GetContactsAsync(Token)).Count);
                Assert.Equal(8, (await session.GetChannelsAsync(Token)).Count);
                await session.DrainMessagesAsync(Token);
                await session.StopAsync("test", Token);
                Assert.NotNull((await storage.Sessions.GetAsync(session.SessionId, Token))?.EndedUtc);
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);
            }
            Assert.Equal(2, provider.Created.Count);
            Assert.NotSame(provider.Created[0], provider.Created[1]);
            Assert.Single(await storage.Nodes.GetAllAsync(10, Token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class LocalhostProvider : ICompanionTransportFactory
    {
        public int Port { get; set; }
        public List<IMeshCoreTransport> Created { get; } = [];
        public IMeshCoreTransport Create(ConnectionProfile profile)
        {
            Assert.Equal(ConnectionTransportKind.Ble, profile.Transport);
            var transport = new TcpMeshCoreTransport("127.0.0.1", Port);
            Created.Add(transport);
            return transport;
        }
    }

    private static ConnectionProfile Profile(ConnectionTransportKind kind) => new()
    {
        Id = Guid.NewGuid(), Name = "Test", Transport = kind,
        TcpHost = kind == ConnectionTransportKind.Tcp ? "localhost" : null,
        TcpPort = kind == ConnectionTransportKind.Tcp ? 5000 : null,
        SerialPortName = kind == ConnectionTransportKind.Serial ? "COM_FAKE" : null,
        BaudRate = kind == ConnectionTransportKind.Serial ? 115200 : null,
        BleDeviceId = kind == ConnectionTransportKind.Ble ? "offline" : null,
        CommandTimeoutMilliseconds = 1234, AcknowledgementTimeoutMilliseconds = 5678,
        CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow,
    };
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed record Paths(string DataDirectory) : IAppPaths
    {
        public string DatabasePath => Path.Combine(DataDirectory, "messenger.db");
        public string BackupsDirectory => Path.Combine(DataDirectory, "backups");
    }
}
