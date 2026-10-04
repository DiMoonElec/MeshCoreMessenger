using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Exceptions;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    private static ContactRouteResetRequest RouteRequest(TcpPrivateFixture f, byte peer = 0xA1) =>
        new(f.Node, f.Supervisor.Snapshot.SessionId!.Value, f.Supervisor.Snapshot.Generation, Enumerable.Repeat(peer, 32).ToArray());

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)0x42)]
    [InlineData((byte)0x82)]
    public async Task TcpResetKeepsHistoryDraftAndOtherContactThenNextPrivateSendUsesFlood(byte descriptor)
    {
        await using var f = await TcpPrivateFixture.CreateAsync(autoAck: false, initialRoute: descriptor);
        var request = RouteRequest(f);
        var original = await f.Storage.ConversationDirectory.GetContactDetailsAsync(f.Node, request.PublicKey, CancellationToken);
        Assert.Equal(descriptor, original!.OutPathLength);
        var bob = await f.Storage.ConversationDirectory.GetContactDetailsAsync(f.Node, RouteRequest(f, 0xB2).PublicKey, CancellationToken);
        var draft = await f.Storage.Drafts.SaveAsync(new(f.Node, null, ConversationKind.Contact, request.PublicKey.ToArray()), "Сохранить 👋", DateTimeOffset.UtcNow, CancellationToken);
        var before = await f.Storage.History.GetMessagesAsync(f.Node, draft!.ConversationId, null, 50, CancellationToken);
        var binding = await f.Storage.Directories.GetActiveChannelBindingAsync(f.Node, 0, CancellationToken);
        Assert.Null(await f.Routes.GetUnavailableReasonAsync(request, CancellationToken));
        var result = await f.Routes.ResetAsync(request, CancellationToken);
        Assert.True(result.LocalUpdated); Assert.Null(result.Warning);
        Assert.Equal(request.PublicKey.ToArray(), Assert.Single(f.Server.RouteResets));
        Assert.Empty(f.Server.PrivateTransmissions); Assert.Equal(0, f.Server.ChannelSendCount);
        var current = await f.Storage.ConversationDirectory.GetContactDetailsAsync(f.Node, request.PublicKey, CancellationToken);
        Assert.Equal(byte.MaxValue, current!.OutPathLength);
        Assert.Equal(original.OutPath, current.OutPath); // Firmware retains bytes, descriptor defines route.
        Assert.Equal(bob!.OutPathLength, (await f.Storage.ConversationDirectory.GetContactDetailsAsync(f.Node, bob.PublicKey, CancellationToken))!.OutPathLength);
        Assert.Equal(binding, await f.Storage.Directories.GetActiveChannelBindingAsync(f.Node, 0, CancellationToken));
        Assert.Equal(before.Select(i => i.Id), (await f.Storage.History.GetMessagesAsync(f.Node, draft.ConversationId, null, 50, CancellationToken)).Select(i => i.Id));
        Assert.Equal("Сохранить 👋", (await f.Storage.Drafts.GetAsync(new(f.Node, draft.ConversationId, ConversationKind.Contact, request.PublicKey.ToArray()), CancellationToken))!.Text);
        var sent = await f.Send("После сброса", 0xA1, 1);
        var transmission = Assert.Single(f.Server.PrivateTransmissions);
        Assert.Equal(byte.MaxValue, transmission.RouteDescriptor);
        await f.Server.SendAcknowledgementAsync(transmission.ExpectedAck);
        await f.WaitState(sent.MessageId, SendAttemptState.Delivered);
    }

    [Fact]
    public async Task TcpRouteResetWaitsForAckAndRejectsStaleSessionAndUnknownContactWithoutCommands()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(autoAck: false);
        var request = RouteRequest(f);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Routes.ResetAsync(request with { Generation = request.Generation + 1 }, CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Routes.ResetAsync(RouteRequest(f, 0xEE), CancellationToken));
        var sent = await f.Send("Pending", 0xA1, 1);
        Assert.NotNull(await f.Routes.GetUnavailableReasonAsync(request, CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Routes.ResetAsync(request, CancellationToken));
        Assert.Empty(f.Server.RouteResets);
        var wire = Assert.Single(f.Server.PrivateTransmissions);
        await f.Server.SendAcknowledgementAsync(wire.ExpectedAck);
        await f.WaitState(sent.MessageId, SendAttemptState.Delivered);
        await Until(() => !f.Operations.IsBusy(f.Node, ConversationKind.Contact, request.PublicKey));
        Assert.True((await f.Routes.ResetAsync(request, CancellationToken)).LocalUpdated);
        Assert.Single(f.Server.RouteResets); Assert.Single(f.Server.PrivateTransmissions);
        await f.Supervisor.DisconnectAsync(CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Routes.ResetAsync(request, CancellationToken));
        Assert.Single(f.Server.RouteResets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TcpConfirmedResetWithFailedReadbackOrLocalCommitReportsPartialSuccessWithoutRetry(bool failSql)
    {
        await using var f = await TcpPrivateFixture.CreateAsync(autoAck: false, initialRoute: 2);
        var request = RouteRequest(f);
        if (failSql)
        {
            using var connection = new SqliteConnection($"Data Source={f.Paths.DatabasePath}"); connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER FailRoute BEFORE UPDATE OF OutPathLength ON Contacts BEGIN SELECT RAISE(ABORT, 'route failure'); END;";
            command.ExecuteNonQuery();
        }
        else f.Server.RejectContactsReadback = true;
        var result = await f.Routes.ResetAsync(request, CancellationToken);
        Assert.False(result.LocalUpdated); Assert.StartsWith("Маршрут сброшен.", result.Warning);
        Assert.Equal(byte.MaxValue, f.Server.ContactRoutes[0xA1]);
        Assert.Equal((byte)2, (await f.Storage.ConversationDirectory.GetContactDetailsAsync(f.Node, request.PublicKey, CancellationToken))!.OutPathLength);
        Assert.Single(f.Server.RouteResets); Assert.Empty(f.Server.PrivateTransmissions);
    }

    [Fact]
    public async Task TcpRejectedResetPreservesRouteAndDoesNotSendOrRetry()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(autoAck: false, initialRoute: 2);
        f.Server.RejectRouteReset = true;
        await Assert.ThrowsAsync<MeshCoreCommandException>(() => f.Routes.ResetAsync(RouteRequest(f), CancellationToken));
        Assert.Equal((byte)2, f.Server.ContactRoutes[0xA1]);
        Assert.Single(f.Server.RouteResets); Assert.Empty(f.Server.PrivateTransmissions);
    }

    [Fact]
    public async Task TcpResetOwnsCopiedTargetAndBlocksSameRecipientUntilReadbackCommit()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(autoAck: false, initialRoute: 2);
        var key = Enumerable.Repeat((byte)0xA1, 32).ToArray();
        var request = RouteRequest(f) with { PublicKey = key };
        var entered = NewGate(); var release = NewGate();
        f.Server.BeforeSingleContactResponse = async () => { entered.TrySetResult(); await release.Task; };
        var reset = f.Routes.ResetAsync(request, CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken);
        Array.Fill(key, (byte)0xB2);
        Assert.Throws<InvalidOperationException>(() => f.Operations.BeginSend(f.Node, ConversationKind.Contact, RouteRequest(f).PublicKey));
        using (f.Operations.BeginSend(f.Node, ConversationKind.Contact, RouteRequest(f, 0xB2).PublicKey)) { }
        release.TrySetResult();
        var result = await reset;
        Assert.All(result.PublicKey.ToArray(), value => Assert.Equal((byte)0xA1, value));
        Assert.True(result.LocalUpdated);
        Assert.False(f.Operations.IsBusy(f.Node, ConversationKind.Contact, RouteRequest(f).PublicKey));
        Assert.Empty(f.Server.PrivateTransmissions);
    }
}
