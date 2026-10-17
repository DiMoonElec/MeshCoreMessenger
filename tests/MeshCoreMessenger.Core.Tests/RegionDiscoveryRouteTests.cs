using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    private sealed class RouteState
    {
        internal Contact? Current;
        internal int Writes;
        internal int Reads;
        internal Func<int, CancellationToken, Task>? BeforeWrite;
        internal Action<int>? AfterWrite;
        internal Action<int>? BeforeRead;
        internal Func<CancellationToken, Task<RegionNamesResponse>>? Request;
    }
    private sealed class RegionRouteFixture : IAsyncDisposable
    {
        internal required Fixture App;
        internal required RouteState State;
        internal required RegionDiscoveryPlan Plan;
        internal required RegionDiscoveryRouteWorkflow Workflow;
        internal Client Client => App.Clients.Current!;
        internal static async Task<RegionRouteFixture> Create(bool known = true, byte route = 255)
        {
            var app = await Fixture.CreateAsync();
            var original = Repeater(1, route, Enumerable.Repeat((byte)0x11,64).ToArray()) with { Flags = 5 };
            var state = new RouteState { Current = known ? original : null };
            app.Clients.Configure = client =>
            {
                client.PreflightContacts = known ? [original, Repeater(2)] : [];
                client.ContactRead = (key, _) =>
                {
                    state.Reads++; state.BeforeRead?.Invoke(state.Reads);
                    return state.Current is { } current && current.PublicKey.Span.SequenceEqual(key.Span) ? Task.FromResult(current) :
                        Task.FromException<Contact>(new MeshCoreCommandException(CommandType.GetContactByKey, MeshCoreErrorCode.NotFound));
                };
                client.ContactWrite = async (contact, token) =>
                {
                    state.Writes++;
                    if (state.BeforeWrite is not null) await state.BeforeWrite(state.Writes, token);
                    state.Current = new(contact.PublicKey.ToArray(), contact.AdvertisementType, contact.Flags, contact.OutPathLength,
                        contact.OutPath.ToArray(), contact.Name, contact.LastAdvertTimestamp, contact.AdvertisementLatitude, contact.AdvertisementLongitude,
                        (state.Current?.LastModified ?? 0) + 1);
                    state.AfterWrite?.Invoke(state.Writes);
                };
                client.RegionRequest = (_, _, token) => state.Request?.Invoke(token) ?? Task.FromResult(new RegionNamesResponse(1, 2, Array.AsReadOnly(new[] { "Area" })));
            };
            await app.Connect(); await using var owner = app.Acquire();
            // Only the requested neighbor is selected; the second known contact is deliberately retained in SQLite.
            var plans = await new RegionDiscoveryPreflight(app.Gateway, TimeProvider.System).PrepareAsync(owner.Owner, [Neighbor(1)], cancellationToken: CancellationToken);
            return new() { App = app, State = state, Plan = plans.Plans.Single(p => p.GetPublicKey()[^1] == 1),
                Workflow = new(app.Gateway, app.Storage.Directories, app.Operations, TimeProvider.System) { CleanupTimeout = TimeSpan.FromMilliseconds(300) } };
        }
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    [Fact]
    public async Task PreparedRouteOwnsContactUntilTerminalResponseThenRestoresExactConfiguration()
    {
        await using var f = await RegionRouteFixture.Create(); var entered = NewGate(); var release = NewGate();
        f.State.Request = async token => { Assert.Equal(0, f.State.Current!.OutPathLength); entered.TrySetResult(); await release.Task.WaitAsync(token); return new(1, 2, ["Area"]); };
        var operation = f.Workflow.RequestAsync(f.Plan, new(AllowTemporaryZeroHop: true), cancellationToken: CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken);
        Assert.False(operation.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => f.App.Operations.BeginSend(f.Plan.NodeId, ConversationKind.Contact, f.Plan.GetPublicKey()));
        Assert.Throws<InvalidOperationException>(() => f.App.Operations.BeginExclusive(f.Plan.NodeId, ConversationKind.Contact, f.Plan.GetPublicKey()));
        using (f.App.Operations.BeginSend(f.Plan.NodeId, ConversationKind.Contact, RepeaterKey(9))) { }
        release.TrySetResult(); var result = await operation;
        Assert.Equal(RegionRoutePreparationOutcome.ZeroHopPrepared, result.Preparation);
        Assert.Equal(RegionRouteRestorationOutcome.Restored, result.Restoration); Assert.NotNull(result.Response);
        Assert.Equal(2, f.State.Writes); Assert.Equal(1, f.Client.RegionRequestCount);
        Assert.Equal(255, f.State.Current!.OutPathLength); Assert.Equal(5, f.State.Current.Flags);
        Assert.All(f.State.Current.OutPath.ToArray(), b => Assert.Equal((byte)0x11, b));
        var contacts = await f.App.Storage.Directories.GetCurrentContactsByPrefixAsync(f.Plan.NodeId, RepeaterKey(1)[..6], CancellationToken);
        Assert.Equal(2, contacts.Count); Assert.Equal((byte?)255, contacts.Single(c => c.PublicKey[^1] == 1).OutPathLength);
        Assert.NotNull(await f.App.Storage.Directories.GetActiveChannelBindingAsync(f.Plan.NodeId, 7, CancellationToken));
    }

    [Theory]
    [InlineData("after-set")]
    [InlineData("request-cancel")]
    [InlineData("request-timeout")]
    public async Task CancelOrRemoteTimeoutAfterAcceptedMutationRestoresWithoutReplaying(string phase)
    {
        await using var f = await RegionRouteFixture.Create(); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        if (phase == "after-set") f.State.AfterWrite = count => { if (count == 1) cancel.Cancel(); };
        if (phase == "request-cancel") f.State.Request = token => { cancel.Cancel(); return Task.FromCanceled<RegionNamesResponse>(token); };
        if (phase == "request-timeout") f.State.Request = _ => throw new MeshCoreTimeoutException("Remote", TimeSpan.FromMilliseconds(10));
        var result = await f.Workflow.RequestAsync(f.Plan, new(AllowTemporaryZeroHop: true), cancellationToken: cancel.Token);
        Assert.Null(result.Response); Assert.NotNull(result.Error); Assert.Equal(RegionRouteRestorationOutcome.Restored, result.Restoration);
        Assert.Equal(255, f.State.Current!.OutPathLength); Assert.Equal(2, f.State.Writes);
        Assert.Equal(phase == "after-set" ? 0 : 1, f.Client.RegionRequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewRouteOrAdvertPreventsRestoreAndCommitsObservedState(bool advertOnly)
    {
        await using var f = await RegionRouteFixture.Create();
        f.State.Request = _ =>
        {
            if (advertOnly) f.Client.EmitAdvertisement(f.State.Current!);
            else f.State.Current = f.State.Current! with { OutPathLength = 1, OutPath = Enumerable.Repeat((byte)0x22,64).ToArray() };
            return Task.FromResult(new RegionNamesResponse(1, 2, ["Area"]));
        };
        var result = await f.Workflow.RequestAsync(f.Plan, new(AllowTemporaryZeroHop: true), cancellationToken: CancellationToken);
        Assert.Equal(RegionRouteRestorationOutcome.Conflict, result.Restoration); Assert.NotNull(result.Response); Assert.Equal(1, f.State.Writes);
        Assert.Equal(advertOnly ? 0 : 1, f.State.Current!.OutPathLength);
    }

    [Theory]
    [InlineData("reject-prepare")]
    [InlineData("proof-timeout")]
    [InlineData("restore-reject")]
    [InlineData("restore-timeout")]
    public async Task FaultsHaveExplicitOutcomesAndNeverReplay(string phase)
    {
        await using var f = await RegionRouteFixture.Create();
        var proofRead = f.State.Reads + 2;
        if (phase == "proof-timeout") f.State.BeforeRead = count => { if (count == proofRead) throw new MeshCoreTimeoutException("Readback", TimeSpan.FromMilliseconds(10)); };
        f.State.BeforeWrite = (count, token) =>
        {
            if (phase == "reject-prepare" && count == 1 || phase == "restore-reject" && count == 2)
                throw new MeshCoreCommandException(CommandType.AddOrUpdateContact, MeshCoreErrorCode.TableFull);
            return phase == "restore-timeout" && count == 2 ? Task.Delay(Timeout.InfiniteTimeSpan, token) : Task.CompletedTask;
        };
        var result = await f.Workflow.RequestAsync(f.Plan, new(AllowTemporaryZeroHop: true), cancellationToken: CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken);
        Assert.Equal(phase.StartsWith("restore") ? 1 : 0, f.Client.RegionRequestCount);
        if (phase == "reject-prepare") Assert.Equal(RegionRoutePreparationOutcome.Rejected, result.Preparation);
        else if (phase == "proof-timeout") Assert.Equal(RegionRouteRestorationOutcome.UnsafeToRestore, result.Restoration);
        else { Assert.NotNull(result.Response); Assert.NotNull(result.CleanupError); Assert.Equal(RegionRouteRestorationOutcome.Failed, result.Restoration); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownContactCreationIsObservedWithoutDeletingItAndTableFullIsPreserved(bool full)
    {
        await using var f = await RegionRouteFixture.Create(known: false);
        f.State.Request = _ =>
        {
            if (full) throw new MeshCoreCommandException(CommandType.SendAnonymousRequest, MeshCoreErrorCode.TableFull);
            f.State.Current = Repeater(1); return Task.FromResult(new RegionNamesResponse(1, 2, []));
        };
        Assert.Equal(RegionRoutePreparationOutcome.Rejected, (await f.Workflow.RequestAsync(f.Plan, new(), cancellationToken: CancellationToken)).Preparation);
        var result = await f.Workflow.RequestAsync(f.Plan, new(AllowContactCreation: true), cancellationToken: CancellationToken);
        Assert.Equal(full ? RegionContactCreationOutcome.NotObserved : RegionContactCreationOutcome.Observed, result.ContactCreation);
        Assert.Equal(full, result.Error is MeshCoreCommandException); Assert.Equal(0, f.State.Writes);
        Assert.Empty(f.Client.Mutations);
        Assert.Equal(full ? 0 : 1, (await f.App.Storage.Directories.GetCurrentContactsByPrefixAsync(f.Plan.NodeId, RepeaterKey(1)[..6], CancellationToken)).Count);
    }

    [Fact]
    public async Task StalePlanAndPrivateOwnershipHaveNoMutationAndDisconnectCannotPublishLateResponse()
    {
        await using var f = await RegionRouteFixture.Create();
        f.Client.EmitAdvertisement(f.State.Current!);
        Assert.Equal(RegionRoutePreparationOutcome.StalePlan, (await f.Workflow.RequestAsync(f.Plan, new(AllowTemporaryZeroHop: true), cancellationToken: CancellationToken)).Preparation);
        Assert.Equal(0, f.State.Writes);
        using (f.App.Operations.BeginSend(f.Plan.NodeId, ConversationKind.Contact, f.Plan.GetPublicKey()))
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Workflow.RequestAsync(f.Plan, new(AllowTemporaryZeroHop: true), cancellationToken: CancellationToken));
        // Prepare a current plan after the observed advert.
        await using var owner = f.App.Acquire();
        var plan = (await new RegionDiscoveryPreflight(f.App.Gateway, TimeProvider.System).PrepareAsync(owner.Owner, [Neighbor(1)], cancellationToken: CancellationToken)).Plans.Single(p => p.GetPublicKey()[^1] == 1);
        var entered = NewGate(); var release = NewGate();
        f.State.Request = async _ => { entered.TrySetResult(); await release.Task; return new(1, 2, ["late"]); };
        var operation = f.Workflow.RequestAsync(plan, new(AllowTemporaryZeroHop: true), cancellationToken: CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken);
        var stop = f.App.Supervisor.DisconnectAsync(CancellationToken);
        await Until(() => f.Client.State == MeshCoreSharp.MeshCoreConnectionState.Disconnected);
        release.TrySetResult(); var result = await operation; await stop;
        Assert.Null(result.Response); Assert.Equal(RegionRouteRestorationOutcome.SessionClosed, result.Restoration); Assert.Equal(1, f.State.Writes);
    }

    [Fact]
    public async Task StorageFailureAfterProofStopsRequestAndDisconnectDuringProofCannotRestoreInAnotherSession()
    {
        await using (var f = await RegionRouteFixture.Create())
        {
            f.App.Execute("CREATE TRIGGER RefuseContactProof BEFORE UPDATE ON Contacts BEGIN SELECT RAISE(ABORT,'injected proof commit failure'); END;");
            var result = await f.Workflow.RequestAsync(f.Plan, new(AllowTemporaryZeroHop: true), cancellationToken: CancellationToken);
            Assert.Equal(RegionRoutePreparationOutcome.Uncertain, result.Preparation);
            Assert.False(result.RequestInvoked); Assert.Equal(0, f.Client.RegionRequestCount); Assert.True(f.App.Outgoing.IsPaused);
            // The lifecycle owns the failed durable barrier; retry writes locally without replaying the request.
            f.App.Execute("DROP TRIGGER RefuseContactProof;"); await f.App.Outgoing.RetryAsync(CancellationToken);
        }
        await using (var f = await RegionRouteFixture.Create())
        {
            var proofRead = f.State.Reads + 2;
            f.State.BeforeRead = count => { if (count == proofRead) f.Client.Fail(); };
            var result = await f.Workflow.RequestAsync(f.Plan, new(AllowTemporaryZeroHop: true), cancellationToken: CancellationToken);
            Assert.Equal(RegionRoutePreparationOutcome.Uncertain, result.Preparation);
            Assert.Equal(RegionRouteRestorationOutcome.SessionClosed, result.Restoration);
            Assert.Equal(0, f.Client.RegionRequestCount); Assert.Equal(1, f.State.Writes);
        }
    }

    [Fact]
    public async Task ReadyDirectUsesNoMutationAndSingleContactReadbackIsNodeSessionAndTimeGuarded()
    {
        await using (var f = await RegionRouteFixture.Create(route: 0))
        {
            var result = await f.Workflow.RequestAsync(f.Plan, new(), cancellationToken: CancellationToken);
            Assert.NotNull(result.Response); Assert.Equal(RegionRouteRestorationOutcome.NotRequired, result.Restoration);
            Assert.Equal(0, f.State.Writes);
        }
        await using var storage = await RegionStoreTests.Context.Create();
        var now = DateTimeOffset.UtcNow; var snapshot = DirectoryService.ToSnapshot(Repeater(1)); var changed = 0;
        storage.Storage.Directories.ContactRouteCommitted += (_, _) => changed++;
        await storage.Storage.Directories.ApplyContactReadbackAsync(storage.Node, storage.Session, snapshot, now, CancellationToken);
        Assert.Equal(1, changed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.Storage.Directories.ApplyContactReadbackAsync(
            storage.Node, storage.Session, snapshot with { DisplayName = "old" }, now.AddSeconds(-1), CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.Storage.Directories.ApplyContactReadbackAsync(
            storage.OtherNode, storage.Session, snapshot, now, CancellationToken));
        await storage.Storage.Sessions.EndAsync(storage.Session, now, "closed", CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.Storage.Directories.ApplyContactReadbackAsync(
            storage.Node, storage.Session, snapshot, now.AddSeconds(1), CancellationToken));
        Assert.Equal(1, changed);
        Assert.Equal("Repeater", Assert.Single(await storage.Storage.Directories.GetCurrentContactsByPrefixAsync(storage.Node, RepeaterKey(1)[..6], CancellationToken)).DisplayName);
        Assert.Empty(await storage.Storage.Directories.GetCurrentContactsByPrefixAsync(storage.OtherNode, RepeaterKey(1)[..6], CancellationToken));
    }
}
