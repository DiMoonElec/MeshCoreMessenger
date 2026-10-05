using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    [Fact]
    public async Task SuccessfulFloodAckEnrichesRouteReadbackWithoutChangingConfiguredRoute()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(true, 100, autoRetries: true);
        f.Server.LearnRouteOnPrivateSend = true; f.Server.LearnedRouteDescriptor = 0x42;
        var result = await f.Send("P6 learned route", 0xA1, 1);
        await WaitCycle(f, result.MessageId, PrivateDeliveryState.Delivered);
        await Until(async () => (await f.Storage.ContactDeliveries.GetPageAsync(f.Node, RouteRequest(f).PublicKey, cancellationToken: CancellationToken))
            .Items.Single().Evidence.Single().LearnedRoute is not null);
        var success = Assert.Single((await f.Storage.ContactDeliveries.GetPageAsync(f.Node, RouteRequest(f).PublicKey, cancellationToken: CancellationToken)).Items);
        var evidence = Assert.Single(success.Evidence);
        Assert.Equal(PrivateRouteKind.Path, evidence.LearnedRoute!.Kind);
        Assert.Equal(2, evidence.LearnedRoute.HopCount);
        Assert.Equal(PrivateRouteKind.Flood, Assert.Single(evidence.Candidates).ConfiguredRoute!.Kind);
        Assert.Single(f.Server.PrivateTransmissions); Assert.Empty(f.Server.RouteResets);
        await f.Server.SendAcknowledgementAsync(f.Server.PrivateTransmissions.First().ExpectedAck);
        Assert.Single((await f.Storage.ContactDeliveries.GetPageAsync(f.Node, RouteRequest(f).PublicKey, cancellationToken: CancellationToken)).Items);
    }

    [Fact]
    public async Task ReadbackFailureAfterAckKeepsSuccessWithNullLearnedRoute()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 5000, autoRetries: true);
        var result = await f.Send("P6 failed readback", 0xA1, 1);
        await Until(() => f.Server.PrivateTransmissions.Count == 1);
        f.Server.RejectContactsReadback = true;
        await f.Server.SendAcknowledgementAsync(f.Server.PrivateTransmissions.Single().ExpectedAck);
        await WaitCycle(f, result.MessageId, PrivateDeliveryState.Delivered);
        await Until(() => f.Server.SingleContactReads.Count >= 2);
        await Until(() => f.Deliveries!.PendingCount == 0);
        var success = Assert.Single((await f.Storage.ContactDeliveries.GetPageAsync(f.Node, RouteRequest(f).PublicKey, cancellationToken: CancellationToken)).Items);
        Assert.Null(Assert.Single(success.Evidence).LearnedRoute);
        Assert.Single(f.Server.PrivateTransmissions); Assert.Empty(f.Server.RouteResets);
    }

    [Fact]
    public async Task EnrichmentSqlFailureRetainsObservationForRetryWithoutRadioReplay()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(true, 100, autoRetries: true);
        using var connection = SqliteDatabase.CreateConnection(f.Paths.DatabasePath, SqliteOpenMode.ReadWrite);
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER RejectP6 BEFORE UPDATE OF LearnedRouteObservedUtc ON ContactDeliveryEvidence BEGIN SELECT RAISE(ABORT,'enrichment failure'); END;";
        command.ExecuteNonQuery();
        var result = await f.Send("P6 durable readback", 0xA1, 1);
        await WaitCycle(f, result.MessageId, PrivateDeliveryState.Delivered);
        await Until(() => f.Writes!.IsPaused);
        var before = f.Server.SingleContactReads.Count;
        Assert.Single(f.Server.PrivateTransmissions);
        command.CommandText = "DROP TRIGGER RejectP6;"; command.ExecuteNonQuery();
        await f.Writes!.RetryAsync(CancellationToken); await f.Writes.FlushAsync(CancellationToken);
        var evidence = Assert.Single(Assert.Single((await f.Storage.ContactDeliveries.GetPageAsync(f.Node, RouteRequest(f).PublicKey, cancellationToken: CancellationToken)).Items).Evidence);
        Assert.NotNull(evidence.LearnedRoute);
        Assert.Equal(before, f.Server.SingleContactReads.Count);
        Assert.Single(f.Server.PrivateTransmissions);
    }

    [Fact]
    public async Task LateAckAfterRetryExhaustionStillQueuesReadbackForItsExactEvidence()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 100, autoRetries: true);
        var result = await f.Send("P6 very late ACK", 0xA1, 1);
        await WaitCycle(f, result.MessageId, PrivateDeliveryState.Unconfirmed);
        await Until(() => f.Deliveries!.PendingCount == 0);
        await f.Server.SendPathUpdatedAsync(0xA1, 0);
        await f.Server.SendAcknowledgementAsync(f.Server.PrivateTransmissions.First().ExpectedAck);
        await WaitCycle(f, result.MessageId, PrivateDeliveryState.Delivered);
        await Until(async () => (await f.Storage.ContactDeliveries.GetPageAsync(f.Node, RouteRequest(f).PublicKey, cancellationToken: CancellationToken))
            .Items.Single().Evidence.Single().LearnedRoute is not null);
        var record = Assert.Single((await f.Storage.ContactDeliveries.GetPageAsync(f.Node, RouteRequest(f).PublicKey, cancellationToken: CancellationToken)).Items);
        Assert.True(record.WasLate);
        Assert.Equal(1, Assert.Single(Assert.Single(record.Evidence).Candidates).AttemptNumber);
        Assert.Equal(3, f.Server.PrivateTransmissions.Count);
    }
}
