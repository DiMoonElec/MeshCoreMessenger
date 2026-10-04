using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class OutgoingMessageStoreTests
{
    [Fact]
    public async Task LateAckConfirmsExactAttemptAndDuplicateDoesNotChangeRtt()
    {
        await using var f = await Fixture.CreateAsync();
        var request = f.Request();
        var prepared = await f.Storage.OutgoingMessages.PrepareAsync(request, CancellationToken);
        await f.Move(prepared, SendAttemptState.Prepared, SendAttemptState.Sending);
        await f.Accept(prepared, AckExpectation.Expected);
        await f.Move(prepared, SendAttemptState.Accepted, SendAttemptState.Unconfirmed);
        var ack = new OutgoingAcknowledgement(f.NodeId, f.SessionId, 0x04030201, 789, request.PreparedUtc.AddSeconds(2));
        var notifications = 0;
        f.Storage.OutgoingMessages.MessageCommitted += (_, _) => notifications++;
        Assert.True(await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(ack with { NodeId = Guid.NewGuid() }, CancellationToken));
        Assert.True(await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(ack with { SessionId = Guid.NewGuid() }, CancellationToken));
        Assert.True(await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(ack with { Tag = 123 }, CancellationToken));
        Assert.True(await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(ack, CancellationToken));
        Assert.True(await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(ack with { RoundTripMilliseconds = 999 }, CancellationToken));
        Assert.Equal(1, notifications);
        var attempt = Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, prepared.MessageId, CancellationToken));
        Assert.Equal(SendAttemptState.Delivered, attempt.State);
        Assert.Equal(789, attempt.RoundTripMilliseconds);
        Assert.Equal(ack.ReceivedUtc, attempt.CompletedUtc);
        await f.Reopen();
        Assert.Equal(SendAttemptState.Delivered, Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, prepared.MessageId, CancellationToken)).State);
    }

    [Theory]
    [InlineData(SendAttemptState.Unconfirmed)]
    [InlineData(SendAttemptState.Delivered)]
    [InlineData(SendAttemptState.Unknown)]
    public async Task CollidingTagsNeverSelectNewestOrStillPendingAttempt(SendAttemptState firstState)
    {
        await using var f = await Fixture.CreateAsync();
        var request = f.Request();
        var first = await f.Storage.OutgoingMessages.PrepareAsync(request, CancellationToken);
        var second = await f.Storage.OutgoingMessages.PrepareAsync(request with { OperationId = Guid.NewGuid() }, CancellationToken);
        foreach (var p in new[] { first, second })
        {
            await f.Move(p, SendAttemptState.Prepared, SendAttemptState.Sending);
            await f.Accept(p, AckExpectation.Expected);
        }
        await f.Move(first, SendAttemptState.Accepted, firstState);
        await f.Move(second, SendAttemptState.Accepted, SendAttemptState.Unconfirmed);
        var ack = new OutgoingAcknowledgement(f.NodeId, f.SessionId, 0x04030201, 100, request.PreparedUtc.AddSeconds(2));
        Assert.True(await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(ack, CancellationToken));
        Assert.Equal(firstState, Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, first.MessageId, CancellationToken)).State);
        Assert.Equal(SendAttemptState.Unconfirmed, Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, second.MessageId, CancellationToken)).State);
    }

    [Fact]
    public async Task EarlyAckSurvivesAcceptedCommitFailureAndCannotBeOverwrittenByTimeout()
    {
        await using var f = await Fixture.CreateAsync();
        var request = f.Request();
        var p = await f.Storage.OutgoingMessages.PrepareAsync(request, CancellationToken);
        await f.Move(p, SendAttemptState.Prepared, SendAttemptState.Sending);
        var writes = new OutgoingAttemptWriteTracker(f.Storage.OutgoingMessages);
        var ack = new OutgoingAcknowledgement(f.NodeId, f.SessionId, 0x04030201, 321, request.PreparedUtc.AddSeconds(2));
        await writes.SaveAcknowledgementAsync(ack, CancellationToken);
        f.Execute("CREATE TRIGGER RejectAck BEFORE UPDATE ON SendAttempts WHEN NEW.State=2 BEGIN SELECT RAISE(ABORT,'ACK write failure'); END;");
        var accepted = f.Transition(p, SendAttemptState.Sending, SendAttemptState.Accepted) with
        { AckExpectation = AckExpectation.Expected, ExpectedAck = new byte[] { 1, 2, 3, 4 }, WireTimestamp = 42 };
        await Assert.ThrowsAsync<OutgoingPersistenceException>(() => writes.SaveAsync(accepted, CancellationToken));
        Assert.True(writes.IsPaused);
        Assert.Equal(1, writes.PendingCount);
        f.Execute("DROP TRIGGER RejectAck;");
        await writes.RetryAsync(CancellationToken);
        await writes.FlushAsync(CancellationToken);
        Assert.False(await writes.SaveAsync(f.Transition(p, SendAttemptState.Accepted, SendAttemptState.Unconfirmed), CancellationToken));
        var attempt = Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, p.MessageId, CancellationToken));
        Assert.Equal(SendAttemptState.Delivered, attempt.State);
        Assert.Equal(321, attempt.RoundTripMilliseconds);
        Assert.Equal(0, writes.PendingCount);
    }

    [Fact]
    public async Task LateAckWriteFailureRetainsEvidenceAndRetryDoesNotTransmit()
    {
        await using var f = await Fixture.CreateAsync();
        var request = f.Request();
        var p = await f.Storage.OutgoingMessages.PrepareAsync(request, CancellationToken);
        await f.Move(p, SendAttemptState.Prepared, SendAttemptState.Sending);
        await f.Accept(p, AckExpectation.Expected);
        await f.Move(p, SendAttemptState.Accepted, SendAttemptState.Unconfirmed);
        var writes = new OutgoingAttemptWriteTracker(f.Storage.OutgoingMessages);
        var ack = new OutgoingAcknowledgement(f.NodeId, f.SessionId, 0x04030201, uint.MaxValue, request.PreparedUtc.AddSeconds(2));
        f.Execute("CREATE TRIGGER RejectLateAck BEFORE UPDATE ON SendAttempts WHEN NEW.State=2 BEGIN SELECT RAISE(ABORT,'late ACK failure'); END;");
        await Assert.ThrowsAsync<OutgoingPersistenceException>(() => writes.SaveAcknowledgementAsync(ack, CancellationToken));
        Assert.Equal(SendAttemptState.Unconfirmed, Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, p.MessageId, CancellationToken)).State);
        Assert.Equal(1, writes.PendingCount);
        f.Execute("DROP TRIGGER RejectLateAck;");
        await writes.RetryAsync(CancellationToken);
        await writes.FlushAsync(CancellationToken);
        var attempt = Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, p.MessageId, CancellationToken));
        Assert.Equal(SendAttemptState.Delivered, attempt.State);
        Assert.Null(attempt.RoundTripMilliseconds); // Protocol u32 exceeds the history model's signed range.
        Assert.Equal(0, writes.PendingCount);
    }

    [Fact]
    public async Task DeletedHistoryAndEvidenceOlderThanAttemptCannotConfirmAnything()
    {
        await using var f = await Fixture.CreateAsync();
        var request = f.Request();
        var p = await f.Storage.OutgoingMessages.PrepareAsync(request, CancellationToken);
        await f.Move(p, SendAttemptState.Prepared, SendAttemptState.Sending);
        await f.Accept(p, AckExpectation.Expected);
        await f.Move(p, SendAttemptState.Accepted, SendAttemptState.Unconfirmed);
        var ack = new OutgoingAcknowledgement(f.NodeId, f.SessionId, 0x04030201, 100, request.PreparedUtc.AddSeconds(-1));
        Assert.True(await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(ack, CancellationToken));
        Assert.Equal(SendAttemptState.Unconfirmed, Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, p.MessageId, CancellationToken)).State);
        f.Execute("DELETE FROM Messages;");
        await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(ack with { ReceivedUtc = request.PreparedUtc.AddSeconds(2) }, CancellationToken);
        Assert.Empty(await f.Storage.History.GetMessagesAsync(f.NodeId, f.ConversationId, null, 10, CancellationToken));
    }
}

public sealed partial class SessionCommandGatewayTests
{
    [Fact]
    public async Task TcpLateAcksConfirmTwoTimedOutMessagesInReverseOrderWithoutSendingAgain()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(autoAck: false, ackTimeout: 100);
        var first = await f.Send("Late first", 0xA1, 1);
        var second = await f.Send("Late second", 0xB2, 2);
        await f.WaitState(first.MessageId, SendAttemptState.Unconfirmed);
        await f.WaitState(second.MessageId, SendAttemptState.Unconfirmed);
        var wire = f.Server.PrivateTransmissions.ToArray();
        await f.Server.SendAcknowledgementAsync(wire[1].ExpectedAck, 456);
        await f.WaitState(second.MessageId, SendAttemptState.Delivered);
        Assert.Equal(SendAttemptState.Unconfirmed, (await f.Read(first.MessageId)).State);
        await f.Server.SendAcknowledgementAsync(wire[0].ExpectedAck, 123);
        await f.WaitState(first.MessageId, SendAttemptState.Delivered);
        Assert.Equal(123, (await f.Read(first.MessageId)).RoundTripMilliseconds);
        Assert.Equal(456, (await f.Read(second.MessageId)).RoundTripMilliseconds);
        Assert.Equal(2, f.Server.PrivateTransmissions.Count);
        Assert.Equal(ConnectionSupervisorState.Online, f.Supervisor.Snapshot.State);
    }

    [Fact]
    public async Task TcpLateAckAfterClearDoesNotRecreateMessage()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(autoAck: false, ackTimeout: 100);
        var sent = await f.Send("Clear before late ACK", 0xA1, 1);
        await f.WaitState(sent.MessageId, SendAttemptState.Unconfirmed);
        var message = await f.Storage.OutgoingMessages.GetAsync(f.Node, sent.MessageId, CancellationToken);
        await Until(() => !f.Operations.IsBusy(f.Node, ConversationKind.Contact, message.Recipient.Identity));
        await f.Clear.ClearAsync(f.Node, message.ConversationId, CancellationToken);
        await f.Server.SendAcknowledgementAsync(f.Server.PrivateTransmissions.Single().ExpectedAck);
        await Task.Delay(100, CancellationToken);
        Assert.Empty(await f.Storage.History.GetMessagesAsync(f.Node, message.ConversationId, null, 10, CancellationToken));
        Assert.Single(f.Server.PrivateTransmissions);
    }
}
