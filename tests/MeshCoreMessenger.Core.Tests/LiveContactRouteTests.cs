using MeshCoreMessenger.Core.Domain;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)0x42)]
    public async Task TcpResetReadsOnlyOneContactAndLearnedRouteUpdatesWithoutReconnect(byte learned)
    {
        await using var f = await TcpPrivateFixture.CreateAsync(autoAck: true, initialRoute: 2);
        var session = f.Supervisor.Snapshot.SessionId;
        var directoryReads = f.Server.ContactsReadCount;
        var notifications = new System.Collections.Concurrent.ConcurrentQueue<byte>();
        f.Storage.Directories.ContactRouteCommitted += (_, commit) =>
        {
            Assert.Equal(f.Node, commit.NodeId);
            Assert.Equal(session, commit.SessionId);
            notifications.Enqueue(commit.PublicKey.Span[0]);
        };
        var result = await f.Routes.ResetAsync(RouteRequest(f), CancellationToken);
        Assert.True(result.LocalUpdated);
        Assert.Single(f.Server.SingleContactReads);
        Assert.Equal(directoryReads, f.Server.ContactsReadCount);
        Assert.Equal(byte.MaxValue, (await f.Storage.ConversationDirectory.GetContactDetailsAsync(f.Node, RouteRequest(f).PublicKey, CancellationToken))!.OutPathLength);
        f.Server.LearnRouteOnPrivateSend = true;
        f.Server.LearnedRouteDescriptor = learned;
        var sent = await f.Send("Learn after reset", 0xA1, 1);
        await f.WaitState(sent.MessageId, SendAttemptState.Delivered);
        await WaitRoute(f, 0xA1, learned);
        Assert.Equal(session, f.Supervisor.Snapshot.SessionId);
        Assert.Equal(ConnectionSupervisorState.Online, f.Supervisor.Snapshot.State);
        Assert.Equal(directoryReads, f.Server.ContactsReadCount);
        await Until(async () => (await f.Storage.ContactDeliveries.GetPageAsync(f.Node, RouteRequest(f).PublicKey, cancellationToken: CancellationToken))
            .Items.Single().Evidence.Single().LearnedRoute is not null);
        Assert.Equal(3, f.Server.SingleContactReads.Count);
        Assert.Equal(new byte[] { 0xA1, 0xA1, 0xA1 }, notifications.ToArray());
        Assert.Single(f.Server.PrivateTransmissions);
        Assert.Equal(byte.MaxValue, f.Server.PrivateTransmissions.Single().RouteDescriptor);
        Assert.Equal(byte.MaxValue, (await f.Storage.ConversationDirectory.GetContactDetailsAsync(f.Node, RouteRequest(f, 0xB2).PublicKey, CancellationToken))!.OutPathLength);
    }

    [Fact]
    public async Task TcpBurstDuringReadbackKeepsFinalRouteAndDoesNotBlockLateAckPump()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(autoAck: false, ackTimeout: 100);
        var sent = await f.Send("ACK while route read waits", 0xA1, 1);
        await f.WaitState(sent.MessageId, SendAttemptState.Unconfirmed);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        f.Server.BeforeSingleContactResponse = async () =>
        {
            if (Interlocked.Increment(ref reads) == 1) { entered.TrySetResult(); await release.Task; }
        };
        try
        {
            await f.Server.SendPathUpdatedAsync(0xA1, 0);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken);
            for (var i = 0; i < 20; i++) await f.Server.SendPathUpdatedAsync(0xA1, 0x42);
            await f.Server.SendAcknowledgementAsync(f.Server.PrivateTransmissions.Single().ExpectedAck, 3000);
            await f.WaitState(sent.MessageId, SendAttemptState.Delivered);
            // The pump processed all PATH_UPDATED events preceding its ACK; its command worker is still blocked.
            Assert.Single(f.Server.SingleContactReads);
        }
        finally { release.TrySetResult(); }
        await WaitRoute(f, 0xA1, 0x42);
        await Until(async () => (await f.Storage.ContactDeliveries.GetPageAsync(f.Node, RouteRequest(f).PublicKey, cancellationToken: CancellationToken))
            .Items.Single().Evidence.Single().LearnedRoute is not null);
        Assert.Equal(3, f.Server.SingleContactReads.Count);
        Assert.Single(f.Server.PrivateTransmissions);
    }

    [Fact]
    public async Task TcpReadbackFailureDoesNotReconnectOrTransmitAndNextNotificationCanRecover()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(autoAck: false);
        var session = f.Supervisor.Snapshot.SessionId;
        f.Server.RejectContactsReadback = true;
        await f.Server.SendPathUpdatedAsync(0xA1, 0);
        await Until(() => f.Server.SingleContactReads.Count == 1);
        // A private send waits behind the failed read and proves the command gate recovered.
        var sent = await f.Send("After failed route read", 0xB2, 1);
        Assert.Equal(SendAttemptState.Accepted, (await f.Read(sent.MessageId)).State);
        Assert.Equal(byte.MaxValue, (await f.Storage.ConversationDirectory.GetContactDetailsAsync(f.Node, RouteRequest(f).PublicKey, CancellationToken))!.OutPathLength);
        f.Server.RejectContactsReadback = false;
        await f.Server.SendPathUpdatedAsync(0xA1, 0x42);
        await WaitRoute(f, 0xA1, 0x42);
        Assert.Equal(session, f.Supervisor.Snapshot.SessionId);
        Assert.Single(f.Server.PrivateTransmissions);
    }

    [Fact]
    public async Task TcpShutdownCancelsBlockedRouteReadBeforeClosingSession()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(autoAck: false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = RouteRequest(f).PublicKey;
        f.Server.BeforeSingleContactResponse = async () => { entered.TrySetResult(); await release.Task; };
        try
        {
            await f.Server.SendPathUpdatedAsync(0xA1, 0);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken);
            await f.Supervisor.ShutdownAsync(CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken);
            Assert.Equal(byte.MaxValue, (await f.Storage.ConversationDirectory.GetContactDetailsAsync(f.Node, key, CancellationToken))!.OutPathLength);
        }
        finally { release.TrySetResult(); }
        Assert.Empty(f.Server.PrivateTransmissions);
    }

    private static async Task WaitRoute(TcpPrivateFixture f, byte peer, byte descriptor)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while ((await f.Storage.ConversationDirectory.GetContactDetailsAsync(f.Node, RouteRequest(f, peer).PublicKey, CancellationToken))!.OutPathLength != descriptor)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Route was not refreshed.");
            await Task.Delay(10, CancellationToken);
        }
    }
}
