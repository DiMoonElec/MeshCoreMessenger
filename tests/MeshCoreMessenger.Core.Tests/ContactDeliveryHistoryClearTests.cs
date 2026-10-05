using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class OutgoingMessageStoreTests
{
    [Fact]
    public async Task ClearDeliveryHistoryScopesFullContactKeyAndNodeAndLateEventsCannotRecreateIt()
    {
        await using var f = await Fixture.CreateAsync();
        var message = await BeginCycle(f);
        var prepared = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(PrivatePreparation(f, message.MessageId), CancellationToken);
        await Expire(f, prepared);
        var target = await ConfirmForReadback(f);
        await f.Storage.OutgoingMessages.EnrichDeliveryRouteAsync(new(target, Route(false)), CancellationToken);
        SeedUnlinkedDelivery(f, f.NodeId, DeliveryContactKey); // Old retention policy may have left this without a message.
        var otherKey = DeliveryContactKey; otherKey[^1] = 99; // Same six-byte prefix, different full identity.
        SeedUnlinkedDelivery(f, f.NodeId, otherKey);
        var otherNode = (await f.Storage.Nodes.FindOrCreateAsync(Enumerable.Repeat((byte)7, 32).ToArray(), "Other node", PrivatePcTime, CancellationToken)).Id;
        SeedUnlinkedDelivery(f, otherNode, DeliveryContactKey);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Storage.HistoryClear.ClearAsync(otherNode, f.ConversationId, CancellationToken));
        await f.Storage.HistoryClear.ClearAsync(f.NodeId, f.Request(channel: true).ConversationId, CancellationToken);
        Assert.Equal(4, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryHistory;")); // Public clear cannot delete contact analytics.

        await f.Storage.HistoryClear.ClearAsync(f.NodeId, f.ConversationId, CancellationToken);
        Assert.Empty((await f.Storage.ContactDeliveries.GetPageAsync(f.NodeId, DeliveryContactKey, cancellationToken: CancellationToken)).Items);
        Assert.Single((await f.Storage.ContactDeliveries.GetPageAsync(f.NodeId, otherKey, cancellationToken: CancellationToken)).Items);
        Assert.Single((await f.Storage.ContactDeliveries.GetPageAsync(otherNode, DeliveryContactKey, cancellationToken: CancellationToken)).Items);
        Assert.Equal(2, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryHistory;"));
        Assert.Equal(2, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryEvidence;"));
        Assert.Equal(2, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryCandidates;"));
        // A readback already in flight is UPDATE-only. Late ACK has no remaining attempts to match.
        Assert.False(await f.Storage.OutgoingMessages.EnrichDeliveryRouteAsync(new(target, Route(false)), CancellationToken));
        await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(new(f.NodeId, f.SessionId, 1, 5, PrivatePcTime), CancellationToken);
        await f.Reopen();
        Assert.Empty((await f.Storage.ContactDeliveries.GetPageAsync(f.NodeId, DeliveryContactKey, cancellationToken: CancellationToken)).Items);
        Assert.Equal(2, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryHistory;"));
    }

    [Fact]
    public async Task AnalyticsWithoutMessagesRemainClearableAndFailureRollsBackAllSnapshots()
    {
        await using var f = await Fixture.CreateAsync();
        SeedUnlinkedDelivery(f, f.NodeId, DeliveryContactKey);
        var service = new HistoryClearService(f.Storage.HistoryClear, new(), new(f.Storage.ReadStates), new OutgoingAttemptWriteTracker(f.Storage.OutgoingMessages));
        var status = await f.Storage.HistoryClear.GetStatusAsync(f.NodeId, f.ConversationId, CancellationToken);
        Assert.Equal(0, status.MessageCount); Assert.True(status.HasDeliveryHistory);
        Assert.Null(await service.GetUnavailableReasonAsync(f.NodeId, f.ConversationId, CancellationToken));
        f.Execute("CREATE TRIGGER RejectAnalyticsClear BEFORE UPDATE OF LastReadSequence ON Conversations BEGIN SELECT RAISE(ABORT,'clear failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => service.ClearAsync(f.NodeId, f.ConversationId, CancellationToken));
        Assert.Equal(1, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryHistory;"));
        Assert.Equal(1, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryEvidence;"));
        Assert.Equal(1, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryCandidates;"));
        f.Execute("DROP TRIGGER RejectAnalyticsClear;");
        Assert.Equal(0, (await service.ClearAsync(f.NodeId, f.ConversationId, CancellationToken)).DeletedCount);
        Assert.Equal("Переписка уже пуста.", await service.GetUnavailableReasonAsync(f.NodeId, f.ConversationId, CancellationToken));
        Assert.Equal(0, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryHistory;"));
        Assert.Equal(0, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryEvidence;"));
        Assert.Equal(0, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryCandidates;"));
    }

    private static void SeedUnlinkedDelivery(Fixture f, Guid node, byte[] key)
    {
        using var connection = f.Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ContactDeliveryHistory
                (Id,NodeId,ContactPublicKey,FirstAckReceivedUtc,PcUtcOffsetMinutes,PcTimeZoneId,WasLate,Attribution)
            VALUES ($delivery,$node,$key,$utc,180,'Europe/Moscow',0,0);
            INSERT INTO ContactDeliveryEvidence (Id,DeliveryId,AckTag,AckReceivedUtc,Attribution)
            VALUES ($evidence,$delivery,X'01000000',$utc,0);
            INSERT INTO ContactDeliveryCandidates
                (EvidenceId,AttemptNumber,WireMessageOrdinal,WireTimestamp,WireAttempt,Phase,RouteDescriptor,RoutePath)
            VALUES ($evidence,1,1,123,0,1,255,X'');
            """;
        command.Parameters.AddWithValue("$delivery", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$evidence", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$node", node.ToString("D"));
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$utc", PrivatePcTime.ToUniversalTime().ToString("O"));
        command.ExecuteNonQuery();
    }
}
