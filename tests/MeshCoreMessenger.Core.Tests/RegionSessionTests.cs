using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class RegionSessionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static CompanionSessionTests.FakeCompanionClient Client(byte key = 1, NodeFloodScope? value = null) => new(
        new SelfInfo(AdvertisementType.Chat, 1, 10, Enumerable.Repeat(key, 32).ToArray(), 0, 0, 0, 0, 0, false, 869.525, 250, 10, 5, "Node"))
    {
        DeviceInfoAction = _ => Task.FromResult(new DeviceInfo(14, 100, 8, null, "fixture", "fake", "1.18", false, 0)),
        RegionReadAction = _ => Task.FromResult(value ?? NodeFloodScope.KnownEmpty),
    };

    [Fact]
    public async Task IdentificationDoesNotOpenCommandsUntilDefaultReadbackIsStored()
    {
        await using var f = await Fixture.Create(); var client = Client();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<NodeFloodScope>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.RegionReadAction = _ => { entered.TrySetResult(); return release.Task; };
        await using var session = await f.Session(client, 1);
        var start = session.StartAsync(Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), Token);
        Assert.Equal(CompanionSessionState.Identifying, session.State);
        Assert.Throws<InvalidOperationException>(() => session.OpenCommands(null!));
        release.TrySetResult(NodeFloodScope.KnownEmpty); await start;
        Assert.Equal(NodeFloodScopeState.KnownEmpty, session.DefaultRegion!.Value.State);
        Assert.True(session.DefaultRegion.IsCurrent(session.SessionId, 1));
        Assert.Equal(RegionSupport.Supported, session.RegionCapabilities.DefaultReadWrite);
        Assert.Equal(RegionSupport.Unknown, session.RegionCapabilities.TemporaryScope);
        var persisted = await f.Storage.Regions.GetNodeSnapshotAsync(session.LocalNodeId!.Value, Token);
        Assert.Equal(RegionSnapshotFreshness.Offline, persisted!.Freshness);
        Assert.Equal(session.SessionId, persisted.SessionId);
        await session.StopAsync(cancellationToken: Token);
        Assert.Equal(RegionSnapshotFreshness.Offline, session.DefaultRegion.Freshness);
        Assert.Equal(["Connect", "Start", "GetDeviceInfo", "GetDefaultFloodScope", "Disconnect", "Flush", "Dispose"], client.Calls);
    }

    [Fact]
    public async Task UnsupportedIsSpecificAndDoesNotPreventLegacyDrainWhileMissingAdapterRemainsUnknown()
    {
        await using var f = await Fixture.Create(); var client = Client();
        client.RegionReadAction = _ => throw new MeshCoreCommandException(CommandType.GetDefaultFloodScope, MeshCoreErrorCode.UnsupportedCommand);
        await using var session = await f.Session(client, 1); await session.StartAsync(Token);
        Assert.Equal(RegionSupport.Unsupported, session.RegionCapabilities.DefaultReadWrite);
        Assert.Equal(RegionSupport.Unknown, session.RegionCapabilities.NeighborDiscovery);
        Assert.Equal(NodeFloodScopeState.Unknown, session.DefaultRegion!.Value.State);
        await session.DrainMessagesAsync(Token); Assert.Contains("Drain", client.Calls);
        await session.StopAsync(cancellationToken: Token);
        var missing = Client(); missing.RegionReadAction = null;
        await using var next = await f.Session(missing, 2); await next.StartAsync(Token);
        Assert.Equal(RegionSupport.Unknown, next.RegionCapabilities.DefaultReadWrite);
        Assert.Equal(NodeFloodScopeState.Unknown, next.DefaultRegion!.Value.State);
    }

    [Fact]
    public async Task ReconnectReadsExternalChangeAndAnotherNodeOnSameProfileGetsIndependentSnapshot()
    {
        await using var f = await Fixture.Create();
        var a = NodeFloodScope.FromPreferences("A", MeshCoreSharp.RegionKeys.DerivePublic("A"));
        await using var first = await f.Session(Client(value: a), 1); await first.StartAsync(Token); await first.StopAsync(cancellationToken: Token);
        await using var second = await f.Session(Client(), 2); await second.StartAsync(Token);
        Assert.Equal(first.LocalNodeId, second.LocalNodeId); Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(NodeFloodScopeState.KnownEmpty, second.DefaultRegion!.Value.State);
        Assert.Equal("A", first.DefaultRegion!.Value.Name);
        await second.StopAsync(cancellationToken: Token);
        await using var third = await f.Session(Client(2, a), 3); await third.StartAsync(Token);
        Assert.NotEqual(second.LocalNodeId, third.LocalNodeId);
        Assert.Equal(NodeFloodScopeState.KnownEmpty, (await f.Storage.Regions.GetNodeSnapshotAsync(second.LocalNodeId!.Value, Token))!.Value.State);
        Assert.Equal("A", (await f.Storage.Regions.GetNodeSnapshotAsync(third.LocalNodeId!.Value, Token))!.Value.Name);
    }

    [Fact]
    public async Task LateReadbackAfterCancellationCannotPersistOrPublishCurrentSnapshot()
    {
        await using var f = await Fixture.Create(); var client = Client();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<NodeFloodScope>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.RegionReadAction = _ => { entered.TrySetResult(); return release.Task; };
        await using var session = await f.Session(client, 1); using var stop = new CancellationTokenSource();
        var pending = session.StartAsync(stop.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), Token);
        stop.Cancel(); release.TrySetResult(NodeFloodScope.KnownEmpty);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.Null(session.DefaultRegion); Assert.Null(await f.Storage.Regions.GetNodeSnapshotAsync(session.LocalNodeId!.Value, Token));
        Assert.Equal(CompanionSessionState.Stopped, session.State); Assert.Contains("Disconnect", client.Calls);
    }

    [Fact]
    public async Task NoCorrelationTimeoutUsesFailedStartTeardownInsteadOfUnsupportedOrFurtherCommands()
    {
        await using var f = await Fixture.Create(); var client = Client();
        client.RegionReadAction = _ => throw new MeshCoreTimeoutException("GetDefaultFloodScopeAsync", TimeSpan.FromMilliseconds(50));
        await using var session = await f.Session(client, 1);
        await Assert.ThrowsAsync<MeshCoreTimeoutException>(() => session.StartAsync(Token));
        Assert.Equal(CompanionSessionState.Failed, session.State); Assert.Null(session.DefaultRegion);
        Assert.Throws<InvalidOperationException>(() => session.OpenCommands(null!));
        Assert.Contains("Disconnect", client.Calls);
        Assert.NotNull((await f.Storage.Sessions.GetAsync(session.SessionId, Token))!.EndedUtc);
    }

    private sealed class Clients : IMeshCoreClientFactory
    {
        internal readonly Queue<ICompanionClient> Pending = [];
        public ICompanionClient Create(ConnectionProfile profile) => Pending.Dequeue();
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal required RegionTestStorage Temporary;
        internal LocalStorage Storage => Temporary.Storage;
        internal required CompanionSessionFactory Factory;
        internal required ConnectionProfile Profile;
        internal required Clients Clients;
        internal static async Task<Fixture> Create()
        {
            var temporary = await RegionTestStorage.CreateAsync(Token);
            var profiles = new ConnectionProfileManager(temporary.Storage.ConnectionProfiles, temporary.Storage.Settings, TimeProvider.System);
            var profile = await profiles.SaveAsync(new() { Name = "Endpoint", Transport = ConnectionTransportKind.Tcp, TcpHost = "localhost", TcpPort = 5000 }, Token);
            var clients = new Clients();
            return new() { Temporary = temporary, Clients = clients, Profile = profile,
                Factory = new CompanionSessionFactory(clients, temporary.Storage.Nodes, temporary.Storage.Sessions,
                    new SessionCompletionTracker(temporary.Storage.Sessions), TimeProvider.System, temporary.Storage.Regions) };
        }
        internal async Task<CompanionSession> Session(ICompanionClient client, long generation)
        { Clients.Pending.Enqueue(client); return await Factory.CreateAsync(Profile, generation, Token); }
        public ValueTask DisposeAsync() => Temporary.DisposeAsync();
    }
}
