using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class OutgoingMessageStoreTests
{
    private static Task<StoredIncomingMessage> Incoming(Fixture f, string text) => f.Storage.IncomingMessages.StoreAsync(
        new(Guid.NewGuid(), f.SessionId, f.NodeId,
            new ContactMessage(f.Request().Recipient.Identity[..6], 1, MessageTextType.Plain, DateTimeOffset.UtcNow,
                text, ReadOnlyMemory<byte>.Empty, 0), DateTimeOffset.UtcNow, null, null), CancellationToken);

    [Fact]
    public async Task ClearDeletesMessagesAndAttemptsPreservesIdentityDraftOtherConversationAndReopen()
    {
        await using var f = await Fixture.CreateAsync();
        var incoming = await Incoming(f, "incoming");
        var sent = await f.Storage.OutgoingMessages.PrepareAsync(f.Request(), CancellationToken);
        await f.Move(sent, SendAttemptState.Prepared, SendAttemptState.Failed);
        var other = await f.Storage.OutgoingMessages.PrepareAsync(f.Request(channel: true), CancellationToken);
        var result = await f.Storage.HistoryClear.ClearAsync(f.NodeId, f.ConversationId, CancellationToken);
        Assert.Equal(2, result.DeletedCount);
        Assert.Equal(sent.LocalSequence, result.CutoffSequence);
        Assert.Empty(await f.Storage.History.GetMessagesAsync(f.NodeId, f.ConversationId, null, 10, CancellationToken));
        Assert.Single(await f.Storage.History.GetMessagesAsync(f.NodeId, f.Request(channel: true).ConversationId, null, 10, CancellationToken));
        Assert.Equal("draft", (await f.Storage.Drafts.GetAsync(new(f.NodeId, f.ConversationId, ConversationKind.Contact, f.Request().Recipient.Identity.ToArray()), CancellationToken))!.Text);
        Assert.Equal(0, (await f.Storage.ReadStates.GetAsync(f.NodeId, f.ConversationId, CancellationToken)).UnreadCount);
        using (var connection = f.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM SendAttempts;";
            Assert.Equal(1L, command.ExecuteScalar());
        }
        Assert.Equal(0, (await f.Storage.HistoryClear.ClearAsync(f.NodeId, f.ConversationId, CancellationToken)).DeletedCount);
        await f.Reopen();
        Assert.Empty(await f.Storage.History.GetMessagesAsync(f.NodeId, f.ConversationId, null, 10, CancellationToken));
        var next = await Incoming(f, "after clear");
        Assert.True(next.LocalSequence > result.CutoffSequence);
        Assert.Single(await f.Storage.History.GetMessagesAsync(f.NodeId, f.ConversationId, null, 10, CancellationToken));
        Assert.Equal(1, (await f.Storage.ReadStates.GetAsync(f.NodeId, f.ConversationId, CancellationToken)).UnreadCount);
    }

    [Fact]
    public async Task IncomingWritesQueuedAfterClearSurviveItsSequenceCutoff()
    {
        await using var f = await Fixture.CreateAsync();
        await Incoming(f, "before");
        var clearing = f.Storage.HistoryClear.ClearAsync(f.NodeId, f.ConversationId, CancellationToken);
        var incoming = Enumerable.Range(0, 8).Select(i => Incoming(f, $"after {i}")).ToArray();
        var result = await clearing;
        var arrived = await Task.WhenAll(incoming);
        Assert.All(arrived, message => Assert.True(message.LocalSequence > result.CutoffSequence));
        Assert.Equal(8, (await f.Storage.History.GetMessagesAsync(f.NodeId, f.ConversationId, null, 10, CancellationToken)).Count);
        Assert.Equal(8, (await f.Storage.ReadStates.GetAsync(f.NodeId, f.ConversationId, CancellationToken)).UnreadCount);
    }

    [Fact]
    public async Task ClearFailureRollsBackMessagesAttemptsAndReadWatermark()
    {
        await using var f = await Fixture.CreateAsync();
        await Incoming(f, "incoming");
        var sent = await f.Storage.OutgoingMessages.PrepareAsync(f.Request(), CancellationToken);
        await f.Move(sent, SendAttemptState.Prepared, SendAttemptState.Failed);
        var before = await f.Storage.ReadStates.GetAsync(f.NodeId, f.ConversationId, CancellationToken);
        f.Execute("CREATE TRIGGER RejectClear BEFORE UPDATE OF LastReadSequence ON Conversations BEGIN SELECT RAISE(ABORT,'clear failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => f.Storage.HistoryClear.ClearAsync(f.NodeId, f.ConversationId, CancellationToken));
        Assert.Equal(2, (await f.Storage.History.GetMessagesAsync(f.NodeId, f.ConversationId, null, 10, CancellationToken)).Count);
        Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, sent.MessageId, CancellationToken));
        Assert.Equal(before, await f.Storage.ReadStates.GetAsync(f.NodeId, f.ConversationId, CancellationToken));
    }

    [Theory]
    [InlineData(SendAttemptState.Prepared)]
    [InlineData(SendAttemptState.Sending)]
    [InlineData(SendAttemptState.Accepted)]
    public async Task ClearRejectsUnfinishedDeliveryButAllowsDormantPreparedWithoutReplay(SendAttemptState state)
    {
        await using var f = await Fixture.CreateAsync();
        var sent = await f.Storage.OutgoingMessages.PrepareAsync(f.Request(), CancellationToken);
        if (state != SendAttemptState.Prepared) await f.Move(sent, SendAttemptState.Prepared, SendAttemptState.Sending);
        if (state == SendAttemptState.Accepted) await f.Accept(sent, AckExpectation.Expected);
        if (state == SendAttemptState.Prepared)
        {
            await f.Reopen(); // Startup leaves a never-transmitted Prepared record dormant.
            var clear = new HistoryClearService(f.Storage.HistoryClear, new(), new(f.Storage.ReadStates), new OutgoingAttemptWriteTracker(f.Storage.OutgoingMessages));
            Assert.Null(await clear.GetUnavailableReasonAsync(f.NodeId, f.ConversationId, CancellationToken));
            Assert.Equal(1, (await clear.ClearAsync(f.NodeId, f.ConversationId, CancellationToken)).DeletedCount);
        }
        else
        {
            Assert.True((await f.Storage.HistoryClear.GetStatusAsync(f.NodeId, f.ConversationId, CancellationToken)).HasPendingSend);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.HistoryClear.ClearAsync(f.NodeId, f.ConversationId, CancellationToken));
            Assert.Single(await f.Storage.History.GetMessagesAsync(f.NodeId, f.ConversationId, null, 10, CancellationToken));
        }
    }

    [Fact]
    public async Task ChannelAcceptedWithoutAckIsClearableAndWrongNodeIsRejected()
    {
        await using var f = await Fixture.CreateAsync();
        var request = f.Request(channel: true);
        var sent = await f.Storage.OutgoingMessages.PrepareAsync(request, CancellationToken);
        await f.Move(sent, SendAttemptState.Prepared, SendAttemptState.Sending);
        await f.Accept(sent, AckExpectation.NotExpected);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Storage.HistoryClear.ClearAsync(Guid.NewGuid(), request.ConversationId, CancellationToken));
        Assert.Equal(1, (await f.Storage.HistoryClear.ClearAsync(f.NodeId, request.ConversationId, CancellationToken)).DeletedCount);
    }

    [Fact]
    public async Task ServiceRetiresLateReadWritesAndPreservesIncomingAfterCutoff()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await Incoming(f, "before");
        var reads = new ConversationReadStateTracker(f.Storage.ReadStates);
        var clear = new HistoryClearService(f.Storage.HistoryClear, new(), reads, new OutgoingAttemptWriteTracker(f.Storage.OutgoingMessages));
        var result = await clear.ClearAsync(f.NodeId, f.ConversationId, CancellationToken);
        var after = await Incoming(f, "after");
        var state = await reads.AdvanceAsync(new(f.NodeId, f.ConversationId, before.MessageId, before.LocalSequence), CancellationToken);
        Assert.False(reads.IsPaused);
        Assert.Equal(0, reads.PendingCount);
        Assert.Equal(1, state.UnreadCount);
        Assert.Equal(after.MessageId, state.FirstUnreadPosition!.MessageId);
        Assert.Equal(result.CutoffSequence, state.LastReadSequence);
    }

    [Fact]
    public async Task ClearSerializesWithQueuedReadAndCancellingBeforeAdmissionPreservesHistory()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await Incoming(f, "before");
        var reads = new ConversationReadStateTracker(f.Storage.ReadStates);
        var outgoing = new OutgoingAttemptWriteTracker(f.Storage.OutgoingMessages);
        var clear = new HistoryClearService(f.Storage.HistoryClear, new(), reads, outgoing);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clear.ClearAsync(f.NodeId, f.ConversationId, cancellation.Token));
        Assert.Single(await f.Storage.History.GetMessagesAsync(f.NodeId, f.ConversationId, null, 10, CancellationToken));
        var read = reads.AdvanceAsync(new(f.NodeId, f.ConversationId, before.MessageId, before.LocalSequence), CancellationToken);
        var clearing = clear.ClearAsync(f.NodeId, f.ConversationId, CancellationToken);
        await Task.WhenAll(read, clearing);
        Assert.False(reads.IsPaused);
        outgoing.ReportFailure(new IOException("retained write"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => clear.ClearAsync(f.NodeId, f.ConversationId, CancellationToken));
    }

    [Fact]
    public async Task OperationAdmissionRejectsClearBeforePrepareAndSendDuringClearWithoutBlockingOtherPeers()
    {
        await using var f = await Fixture.CreateAsync();
        await Incoming(f, "before");
        var operations = new ConversationOperationGuard();
        var clear = new HistoryClearService(f.Storage.HistoryClear, operations, new(f.Storage.ReadStates), new OutgoingAttemptWriteTracker(f.Storage.OutgoingMessages));
        var identity = f.Request().Recipient.Identity;
        using (operations.BeginSend(f.NodeId, ConversationKind.Contact, identity))
        {
            Assert.NotNull(await clear.GetUnavailableReasonAsync(f.NodeId, f.ConversationId, CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => clear.ClearAsync(f.NodeId, f.ConversationId, CancellationToken));
        }
        using (operations.BeginClear(f.NodeId, ConversationKind.Contact, identity))
        {
            Assert.Throws<InvalidOperationException>(() => operations.BeginSend(f.NodeId, ConversationKind.Contact, identity));
            using var other = operations.BeginSend(Guid.NewGuid(), ConversationKind.Contact, identity);
        }
        Assert.Equal(1, (await clear.ClearAsync(f.NodeId, f.ConversationId, CancellationToken)).DeletedCount);
    }
}
