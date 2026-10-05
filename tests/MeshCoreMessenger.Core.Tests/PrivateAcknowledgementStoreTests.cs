using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class OutgoingMessageStoreTests
{
    private static long Scalar(Fixture f, string sql)
    {
        using var connection = f.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task AckOfKnownPhaseDuringFallbackConfirmsMessageAndKeepsFirstRouteEvidence()
    {
        await using var f = await Fixture.CreateAsync();
        var message = await BeginCycle(f, flood: false);
        PreparedPrivateAttempt? fallback = null;
        for (var previous = 0; previous < 4; previous++)
        {
            var prepared = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(
                PrivatePreparation(f, message.MessageId, previous, flood: previous >= 3), CancellationToken);
            await Expire(f, prepared);
            fallback = prepared;
        }
        var ack = new OutgoingAcknowledgement(f.NodeId, f.SessionId, 1, 99, PrivatePcTime.AddMinutes(1), 180, "Europe/Moscow");
        await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(ack, CancellationToken);
        var cycle = (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.NodeId, message.MessageId, CancellationToken))!;
        Assert.Equal(PrivateDeliveryState.Delivered, cycle.State);
        Assert.Equal(ack.ReceivedUtc.ToUniversalTime(), cycle.ConfirmedUtc);
        var attempts = await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, message.MessageId, CancellationToken);
        Assert.Equal(SendAttemptState.Delivered, attempts[0].State);
        Assert.Equal(SendAttemptState.Unconfirmed, attempts[3].State);
        Assert.Equal(1, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryCandidates WHERE WireMessageOrdinal=1 AND WireAttempt=0 AND RouteDescriptor=66;"));
        Assert.Equal(0, Scalar(f, "SELECT WasLate FROM ContactDeliveryHistory;")); // Whole cycle still had a fifth attempt available.
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(
            PrivatePreparation(f, message.MessageId, 4), CancellationToken));
        await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(ack with { Tag = 4, ReceivedUtc = ack.ReceivedUtc.AddSeconds(1) }, CancellationToken);
        await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(ack with { RoundTripMilliseconds = 999 }, CancellationToken);
        Assert.Equal(1, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryHistory;"));
        Assert.Equal(2, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryEvidence;"));
        Assert.Equal(1, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryCandidates WHERE WireMessageOrdinal=2 AND RouteDescriptor=255;"));
        Assert.Equal(cycle.ConfirmedUtc, (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.NodeId, message.MessageId, CancellationToken))!.ConfirmedUtc);
        Assert.NotNull(fallback);
        await f.Reopen();
        Assert.Equal(PrivateDeliveryState.Delivered, (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.NodeId, message.MessageId, CancellationToken))!.State);
    }

    [Fact]
    public async Task CollidingAttemptsOfOneMessageConfirmCycleWithoutChoosingAnAttempt()
    {
        await using var f = await Fixture.CreateAsync();
        var message = await BeginCycle(f, flood: false);
        for (var previous = 0; previous < 2; previous++)
        {
            var prepared = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(
                PrivatePreparation(f, message.MessageId, previous, flood: false), CancellationToken);
            await Expire(f, prepared);
        }
        // Force an actual 32-bit tag collision, not an assumption about hash uniqueness.
        f.Execute("UPDATE SendAttempts SET ExpectedAck=X'01000000';");
        await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(new(f.NodeId, f.SessionId, 1, 10, PrivatePcTime.AddSeconds(2)), CancellationToken);
        Assert.Equal(PrivateDeliveryState.Delivered, (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.NodeId, message.MessageId, CancellationToken))!.State);
        Assert.All(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, message.MessageId, CancellationToken),
            item => Assert.Equal(SendAttemptState.Unconfirmed, item.State));
        Assert.Equal((int)DeliveryAttribution.MultipleCandidates, Scalar(f, "SELECT Attribution FROM ContactDeliveryHistory;"));
        Assert.Equal(2, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryCandidates;"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(
            PrivatePreparation(f, message.MessageId, 2, flood: false), CancellationToken));
    }

    [Fact]
    public async Task CrossMessageCollisionCannotBypassMatchingThroughDirectDeliveredTransition()
    {
        await using var f = await Fixture.CreateAsync();
        var messages = new List<PreparedPrivateAttempt>();
        for (var index = 0; index < 2; index++)
        {
            var message = await BeginCycle(f);
            var prepared = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(PrivatePreparation(f, message.MessageId), CancellationToken);
            await Expire(f, prepared);
            messages.Add(prepared); // Both first attempts have test tag 1.
        }
        var transition = new OutgoingAttemptTransition(f.NodeId, messages[1].Message.MessageId, messages[1].Message.Attempt.Id,
            f.SessionId, SendAttemptState.Unconfirmed, SendAttemptState.Delivered, PrivatePcTime.AddSeconds(3));
        Assert.False(await f.Storage.OutgoingMessages.TransitionAsync(transition, CancellationToken));
        Assert.Equal(0, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryHistory;"));
        foreach (var item in messages)
            Assert.Equal(PrivateDeliveryState.Active, (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.NodeId, item.Message.MessageId, CancellationToken))!.State);
    }

    [Fact]
    public async Task ExhaustionThenLateAckWritesOneAtomicSuccessWithRealPcTime()
    {
        await using var f = await Fixture.CreateAsync();
        var message = await BeginCycle(f);
        for (var previous = 0; previous < 3; previous++)
        {
            var prepared = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(
                PrivatePreparation(f, message.MessageId, previous), CancellationToken);
            await Expire(f, prepared);
        }
        Assert.Equal(PrivateDeliveryState.Unconfirmed, (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.NodeId, message.MessageId, CancellationToken))!.State);
        var ack = new OutgoingAcknowledgement(f.NodeId, f.SessionId, 1, uint.MaxValue, PrivatePcTime.AddDays(-1), -300, "America/New_York");
        var writes = new OutgoingAttemptWriteTracker(f.Storage.OutgoingMessages);
        f.Execute("CREATE TRIGGER RejectDeliveryHistory BEFORE INSERT ON ContactDeliveryHistory BEGIN SELECT RAISE(ABORT,'history failure'); END;");
        var notifications = 0;
        f.Storage.OutgoingMessages.MessageCommitted += (_, _) => notifications++;
        await Assert.ThrowsAsync<OutgoingPersistenceException>(() => writes.SaveAcknowledgementAsync(ack, CancellationToken));
        Assert.True(writes.IsPaused);
        Assert.Equal(1, writes.PendingCount);
        Assert.Equal(PrivateDeliveryState.Unconfirmed, (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.NodeId, message.MessageId, CancellationToken))!.State);
        Assert.Equal(0, Scalar(f, "SELECT COUNT(*) FROM SendAttempts WHERE State=2;"));
        Assert.Equal(0, notifications);
        f.Execute("DROP TRIGGER RejectDeliveryHistory;");
        await writes.RetryAsync(CancellationToken);
        await writes.FlushAsync(CancellationToken);
        await writes.SaveAcknowledgementAsync(ack with { RoundTripMilliseconds = 50 }, CancellationToken);
        var cycle = (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.NodeId, message.MessageId, CancellationToken))!;
        Assert.Equal(PrivateDeliveryState.Delivered, cycle.State);
        Assert.Equal(ack.ReceivedUtc.ToUniversalTime(), cycle.ConfirmedUtc); // Actual clock moved backwards.
        Assert.Equal(1, Scalar(f, "SELECT WasLate FROM ContactDeliveryHistory;"));
        Assert.Equal(-300, Scalar(f, "SELECT PcUtcOffsetMinutes FROM ContactDeliveryHistory WHERE PcTimeZoneId='America/New_York';"));
        Assert.Equal((long)uint.MaxValue, Scalar(f, "SELECT RoundTripMilliseconds FROM ContactDeliveryEvidence;"));
        Assert.Equal(1, notifications);
        Assert.Equal(0, writes.PendingCount);
        Assert.Equal(3, Scalar(f, "SELECT COUNT(*) FROM SendAttempts;")); // Persistence retry adds no transmissions/attempts.
        await f.Storage.HistoryClear.ClearAsync(f.NodeId, f.ConversationId, CancellationToken);
        Assert.Equal(0, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryHistory;"));
        Assert.Equal(0, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryEvidence;"));
        Assert.Equal(0, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryCandidates;"));
    }

    [Fact]
    public async Task EarlierAttemptSuccessHasPriorityOverLaterAttemptTimeout()
    {
        await using var f = await Fixture.CreateAsync();
        var message = await BeginCycle(f);
        var first = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(PrivatePreparation(f, message.MessageId), CancellationToken);
        await Expire(f, first);
        var second = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(PrivatePreparation(f, message.MessageId, 1), CancellationToken);
        var sending = new OutgoingAttemptTransition(f.NodeId, message.MessageId, second.Message.Attempt.Id, f.SessionId,
            SendAttemptState.Prepared, SendAttemptState.Sending, PrivatePcTime.AddSeconds(1));
        await f.Storage.OutgoingMessages.TransitionAsync(sending, CancellationToken);
        await f.Storage.OutgoingMessages.TransitionAsync(sending with
        {
            ExpectedState = SendAttemptState.Sending, State = SendAttemptState.Accepted, AckExpectation = AckExpectation.Expected,
            ExpectedAck = new byte[] {2,0,0,0}, WireTimestamp = second.Capture.WireMessage.Timestamp,
        }, CancellationToken);
        await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(new(f.NodeId, f.SessionId, 1, 15, PrivatePcTime.AddSeconds(2)), CancellationToken);
        Assert.True(await f.Storage.OutgoingMessages.TransitionAsync(sending with
        { ExpectedState = SendAttemptState.Accepted, State = SendAttemptState.Unconfirmed, AtUtc = PrivatePcTime.AddSeconds(3) }, CancellationToken));
        Assert.Equal(PrivateDeliveryState.Delivered, (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.NodeId, message.MessageId, CancellationToken))!.State);
        Assert.Equal(1, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryHistory;"));
        Assert.Equal(1, Scalar(f, "SELECT COUNT(*) FROM SendAttempts WHERE State=3;"));
    }

    [Fact]
    public async Task AckMatchingOldTagWaitsForUnknownInFlightTagBeforeCollisionDecision()
    {
        await using var f = await Fixture.CreateAsync();
        var first = await f.Storage.OutgoingMessages.PrepareAsync(f.Request(), CancellationToken);
        var second = await f.Storage.OutgoingMessages.PrepareAsync(f.Request(), CancellationToken);
        await f.Move(first, SendAttemptState.Prepared, SendAttemptState.Sending);
        await f.Accept(first, AckExpectation.Expected);
        await f.Move(second, SendAttemptState.Prepared, SendAttemptState.Sending);
        var writes = new OutgoingAttemptWriteTracker(f.Storage.OutgoingMessages);
        await writes.SaveAcknowledgementAsync(new(f.NodeId, f.SessionId, 0x04030201, 20, DateTimeOffset.UtcNow), CancellationToken);
        Assert.Equal(0, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryHistory;"));
        await writes.SaveAsync(f.Transition(second, SendAttemptState.Sending, SendAttemptState.Accepted) with
        { AckExpectation = AckExpectation.Expected, ExpectedAck = new byte[] {1,2,3,4}, WireTimestamp = 43 }, CancellationToken);
        Assert.Equal(0, Scalar(f, "SELECT COUNT(*) FROM SendAttempts WHERE State=2;"));
        Assert.Equal(0, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryHistory;"));
    }

    [Fact]
    public async Task KnownTagStillConfirmsAfterCleanupMarkedAttemptUnknown()
    {
        await using var f = await Fixture.CreateAsync();
        var message = await BeginCycle(f);
        var prepared = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(PrivatePreparation(f, message.MessageId), CancellationToken);
        await Expire(f, prepared);
        f.Execute("UPDATE SendAttempts SET State=5; UPDATE PrivateDeliveryCycles SET State=5;");
        await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(new(f.NodeId, f.SessionId, 1, 20, PrivatePcTime.AddSeconds(2)), CancellationToken);
        Assert.Equal(PrivateDeliveryState.Delivered, (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.NodeId, message.MessageId, CancellationToken))!.State);
        Assert.Equal(1, Scalar(f, "SELECT COUNT(*) FROM SendAttempts WHERE State=2;"));
        Assert.Equal(1, Scalar(f, "SELECT WasLate FROM ContactDeliveryHistory;"));
    }

    [Fact]
    public async Task EarlyAckUsesWriterOrderWhenClockMovesBackwards()
    {
        await using var f = await Fixture.CreateAsync();
        var message = await BeginCycle(f);
        var prepared = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(PrivatePreparation(f, message.MessageId), CancellationToken);
        var writes = new OutgoingAttemptWriteTracker(f.Storage.OutgoingMessages);
        var ack = new OutgoingAcknowledgement(f.NodeId, f.SessionId, 1, 30, PrivatePcTime.AddDays(-1));
        await writes.SaveAcknowledgementAsync(ack, CancellationToken); // No Sending yet: discard, never apply to a future send.
        await writes.SaveAsync(f.Transition(prepared.Message, SendAttemptState.Prepared, SendAttemptState.Sending), CancellationToken);
        await writes.SaveAcknowledgementAsync(ack, CancellationToken); // Now retain until MSG_SENT is persisted.
        var accepted = new OutgoingAttemptTransition(f.NodeId, message.MessageId, prepared.Message.Attempt.Id, f.SessionId,
            SendAttemptState.Sending, SendAttemptState.Accepted, PrivatePcTime.AddSeconds(1), AckExpectation.Expected,
            prepared.Capture.WireMessage.Timestamp, new byte[] {1,0,0,0});
        await writes.SaveAsync(accepted, CancellationToken);
        Assert.Equal(PrivateDeliveryState.Delivered, (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.NodeId, message.MessageId, CancellationToken))!.State);
        Assert.False(await writes.SaveAsync(accepted with { ExpectedState = SendAttemptState.Accepted, State = SendAttemptState.Unconfirmed }, CancellationToken));
        Assert.Equal(1, Scalar(f, "SELECT COUNT(*) FROM ContactDeliveryHistory;"));
    }
}
