using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class RegionDefaultServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task SetClearAndMismatchPublishActualStateOnlyAfterCommit()
    {
        await using var f = await Fixture.Create();
        var definition = RegionDefinition.Public("Area");
        var result = await f.Service.SetNodeDefaultAsync(f.Owner, definition, Token);
        Assert.Equal(RegionDefaultChangeOutcome.Applied, result.Outcome);
        Assert.True(result.CommandAccepted);
        Assert.Equal("Area", f.Gateway.GetDefaultRegion(f.Owner.NodeId)!.Value.Name);
        Assert.Equal(1, f.Session.DefaultRegion!.Revision);
        await using var nextLease = f.Gateway.Acquire(f.Owner.NodeId, new NodeCommandTarget(), Token);
        Assert.Equal(1, nextLease.Owner.DefaultRegion!.Revision);
        Assert.Equal(0, f.Owner.DefaultRegion!.Revision); // Captured owners stay immutable.
        Assert.Equal("Area", (await f.Temporary.Storage.Regions.GetNodeSnapshotAsync(f.Owner.NodeId, Token))!.Value.Name);
        Assert.Equal(RegionDefaultChangeOutcome.Applied, (await f.Service.ClearNodeDefaultAsync(f.Owner, Token)).Outcome);
        Assert.Equal(NodeFloodScopeState.KnownEmpty, f.Session.DefaultRegion!.Value.State);
        Assert.Equal(2, f.Session.DefaultRegion.Revision);
        f.Client.RegionWriteAction = (_, _) => Task.CompletedTask; // Device reports acceptance but keeps an empty default.
        result = await f.Service.SetNodeDefaultAsync(f.Owner, definition, Token);
        Assert.Equal(RegionDefaultChangeOutcome.ReadbackMismatch, result.Outcome);
        Assert.Equal(NodeFloodScopeState.KnownEmpty, result.Observed!.Value.State);
        Assert.Equal(3, f.Session.DefaultRegion.Revision);
        Assert.Equal(2, f.Client.Calls.Count(c => c == "SetDefaultFloodScope"));
        Assert.Equal(1, f.Client.Calls.Count(c => c == "ClearDefaultFloodScope"));
    }

    [Fact]
    public async Task ExplicitRejectionPreservesPreviousObservationAndDoesNotReadOrRetry()
    {
        await using var f = await Fixture.Create();
        f.Client.RegionWriteAction = (_, _) => throw new MeshCoreCommandException(CommandType.SetDefaultFloodScope, MeshCoreErrorCode.UnsupportedCommand);
        var initial = f.Session.DefaultRegion;
        var result = await f.Service.SetNodeDefaultAsync(f.Owner, RegionDefinition.Public("Area"), Token);
        Assert.Equal(RegionDefaultChangeOutcome.Rejected, result.Outcome);
        Assert.False(result.CommandAccepted);
        Assert.Same(initial, f.Gateway.GetDefaultRegion(f.Owner.NodeId));
        Assert.Single(f.Client.Calls, c => c == "GetDefaultFloodScope"); // Startup only.
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("timeout")]
    [InlineData("uncertain-set")]
    public async Task FailureBetweenPhasesPreservesUncertaintyWithoutReplay(string failure)
    {
        await using var f = await Fixture.Create();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        if (failure == "cancel") f.Client.RegionWriteAction = (_, _) => { cancel.Cancel(); return Task.CompletedTask; };
        if (failure == "timeout") f.Client.RegionReadAction = _ => throw new MeshCoreTimeoutException("GET", TimeSpan.FromMilliseconds(10));
        if (failure == "uncertain-set") f.Client.RegionWriteAction = (_, _) => throw new IOException("Write outcome unknown");
        var result = await f.Service.SetNodeDefaultAsync(f.Owner, RegionDefinition.Public("Area"), cancel.Token);
        Assert.Equal(failure == "uncertain-set" ? RegionDefaultChangeOutcome.CommandUncertain : RegionDefaultChangeOutcome.ReadbackFailed, result.Outcome);
        Assert.Equal(failure != "uncertain-set", result.CommandAccepted);
        Assert.Equal(NodeFloodScopeState.Unknown, f.Gateway.GetDefaultRegion(f.Owner.NodeId)!.Value.State);
        Assert.Equal(NodeFloodScopeState.Unknown, (await f.Temporary.Storage.Regions.GetNodeSnapshotAsync(f.Owner.NodeId, Token))!.Value.State);
        Assert.Single(f.Client.Calls, c => c == "SetDefaultFloodScope");
        Assert.Equal(0, f.Session.DefaultRegion!.Revision);
    }

    [Fact]
    public async Task ConcurrentSendWaitsForReadbackAndCommittedPublication()
    {
        await using var f = await Fixture.Create();
        var entered = Gate(); var release = Gate();
        f.Client.RegionReadAction = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); return f.DeviceDefault; };
        var mutation = f.Service.SetNodeDefaultAsync(f.Owner, RegionDefinition.Public("Area"), Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
        await using var sending = f.Gateway.Acquire(f.Owner.NodeId, new NodeCommandTarget(), Token);
        var send = f.Scope.Invoke(sending, (_, _) => Task.FromResult(f.Gateway.GetDefaultRegion(f.Owner.NodeId)), sending: false);
        // Use an owned operation under the same region gate as text invocation (no radio TX in this test).
        var orderedSend = f.Scope.WithRegionStateAsync(sending, () => Task.FromResult(f.Gateway.GetDefaultRegion(f.Owner.NodeId)));
        Assert.False(orderedSend.IsCompleted);
        Assert.Equal(NodeFloodScopeState.Unknown, (await send)!.Value.State);
        release.TrySetResult();
        Assert.Equal(RegionDefaultChangeOutcome.Applied, (await mutation).Outcome);
        Assert.Equal("Area", (await orderedSend)!.Value.Name);
        Assert.Equal(1, (await orderedSend)!.Revision);
    }

    [Fact]
    public async Task OldOwnerIsRejectedAndShutdownCannotPublishLateReadback()
    {
        await using var f = await Fixture.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ClearNodeDefaultAsync(f.Owner with { SessionId = Guid.NewGuid() }, Token));
        var entered = Gate(); var release = Gate();
        f.Client.RegionReadAction = async _ => { entered.TrySetResult(); await release.Task; return f.DeviceDefault; };
        var mutation = f.Service.SetNodeDefaultAsync(f.Owner, RegionDefinition.Public("Area"), Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
        var stop = f.Session.StopAsync(cancellationToken: Token);
        Assert.Null(f.Gateway.GetDefaultRegion(f.Owner.NodeId));
        release.TrySetResult();
        Assert.Equal(RegionDefaultChangeOutcome.ReadbackFailed, (await mutation).Outcome);
        await stop;
        Assert.Null(f.Gateway.GetDefaultRegion(f.Owner.NodeId));
        Assert.Equal(NodeFloodScopeState.Unknown, (await f.Temporary.Storage.Regions.GetNodeSnapshotAsync(f.Owner.NodeId, Token))!.Value.State);
    }

    [Fact]
    public async Task StorageFailureDoesNotPublishSuccessAndPausesAdmission()
    {
        await using var f = await Fixture.Create();
        using (var connection = SqliteDatabase.CreateConnection(f.Temporary.Paths.DatabasePath, SqliteOpenMode.ReadWrite))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER RejectRegionUpdate BEFORE UPDATE ON NodeRegionSnapshots BEGIN SELECT RAISE(ABORT,'injected failure'); END;";
            command.ExecuteNonQuery();
        }
        var result = await f.Service.SetNodeDefaultAsync(f.Owner, RegionDefinition.Public("Area"), Token);
        Assert.Equal(RegionDefaultChangeOutcome.PersistenceFailed, result.Outcome);
        Assert.True(result.CommandAccepted); Assert.Equal("Area", result.Observed!.Value.Name);
        Assert.Equal(NodeFloodScopeState.Unknown, f.Session.DefaultRegion!.Value.State);
        Assert.Equal(0, f.Session.DefaultRegion.Revision);
        Assert.True(f.Outgoing.IsPaused);
        Assert.Throws<OutgoingPersistenceException>(() => f.Gateway.Acquire(f.Owner.NodeId, new NodeCommandTarget(), Token));
    }

    private sealed class Clients(ICompanionClient client) : IMeshCoreClientFactory
    { public ICompanionClient Create(ConnectionProfile profile) => client; }

    private sealed class Fixture : IAsyncDisposable
    {
        internal required RegionTestStorage Temporary;
        internal required CompanionSessionTests.FakeCompanionClient Client;
        internal required CompanionSession Session;
        internal required SessionCommandGateway Gateway;
        internal required SessionCommandScope Scope;
        internal required RegionService Service;
        internal required OutgoingAttemptWriteTracker Outgoing;
        internal SessionCommandOwner Owner => Scope.Owner;
        internal NodeFloodScope DeviceDefault = NodeFloodScope.KnownEmpty;
        internal static async Task<Fixture> Create()
        {
            var temporary = await RegionTestStorage.CreateAsync(Token); var storage = temporary.Storage;
            var client = new CompanionSessionTests.FakeCompanionClient(new SelfInfo(AdvertisementType.Chat, 1, 10,
                Enumerable.Repeat((byte)1, 32).ToArray(), 0, 0, 0, 0, 0, false, 869.525, 250, 10, 5, "Node"))
            { RegionReadAction = _ => Task.FromResult(NodeFloodScope.KnownEmpty) };
            var profiles = new ConnectionProfileManager(storage.ConnectionProfiles, storage.Settings, TimeProvider.System);
            var profile = await profiles.SaveAsync(new() { Name = "Test", Transport = ConnectionTransportKind.Tcp, TcpHost = "localhost", TcpPort = 5000 }, Token);
            var factory = new CompanionSessionFactory(new Clients(client), storage.Nodes, storage.Sessions, new SessionCompletionTracker(storage.Sessions), TimeProvider.System, storage.Regions);
            var session = await factory.CreateAsync(profile, 1, Token); await session.StartAsync(Token);
            var outgoing = new OutgoingAttemptWriteTracker(storage.OutgoingMessages);
            var gateway = new SessionCommandGateway(storage.Directories, storage.ConversationDirectory, storage.OutgoingMessages, outgoing, TimeProvider.System);
            var scope = session.OpenCommands(gateway);
            var f = new Fixture { Temporary = temporary, Client = client, Session = session, Gateway = gateway, Scope = scope,
                Outgoing = outgoing, Service = new(storage.Regions, TimeProvider.System, gateway) };
            client.RegionReadAction = _ => Task.FromResult(f.DeviceDefault);
            client.RegionWriteAction = (definition, _) => { f.DeviceDefault = definition is null ? NodeFloodScope.KnownEmpty : NodeFloodScope.FromPreferences(definition.Name, definition.Key); return Task.CompletedTask; };
            return f;
        }
        public async ValueTask DisposeAsync()
        {
            try { await Session.DisposeAsync(); }
            catch (OutgoingPersistenceException) when (Outgoing.IsPaused) { }
            await Temporary.DisposeAsync();
        }
    }
}
