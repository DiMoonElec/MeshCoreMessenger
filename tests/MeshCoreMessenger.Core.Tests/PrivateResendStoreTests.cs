using MeshCoreMessenger.Core.Domain;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class OutgoingMessageStoreTests
{
    private static async Task<PreparedOutgoingMessage> LegacyResendSource(Fixture f, SendAttemptState state)
    {
        var source = await f.Storage.OutgoingMessages.PrepareAsync(f.Request(), CancellationToken);
        if (state == SendAttemptState.Prepared) return source;
        if (state == SendAttemptState.Failed) { await f.Move(source, SendAttemptState.Prepared, state); return source; }
        await f.Move(source, SendAttemptState.Prepared, SendAttemptState.Sending);
        if (state == SendAttemptState.Sending) return source;
        await f.Accept(source, AckExpectation.Expected);
        if (state != SendAttemptState.Accepted) await f.Move(source, SendAttemptState.Accepted, state);
        return source;
    }

    [Theory]
    [InlineData(SendAttemptState.Prepared, false)]
    [InlineData(SendAttemptState.Sending, false)]
    [InlineData(SendAttemptState.Accepted, false)]
    [InlineData(SendAttemptState.Delivered, false)]
    [InlineData(SendAttemptState.Unknown, false)]
    [InlineData(SendAttemptState.Unconfirmed, true)]
    [InlineData(SendAttemptState.Failed, true)]
    public async Task FreshPrivatePrepareRequiresUndeliveredSourceAndPreservesOriginal(SendAttemptState state, bool allowed)
    {
        await using var f = await Fixture.CreateAsync();
        var source = await LegacyResendSource(f, state);
        var request = f.Request() with { PrivateResendSourceId = source.MessageId };
        if (!allowed)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.PrepareAsync(request, CancellationToken));
            Assert.Single(await f.Storage.History.GetMessagesAsync(f.NodeId, f.ConversationId, null, 10, CancellationToken));
        }
        else
        {
            var fresh = await f.Storage.OutgoingMessages.PrepareAsync(request, CancellationToken);
            Assert.NotEqual(source.MessageId, fresh.MessageId); Assert.True(fresh.LocalSequence > source.LocalSequence);
            Assert.Equal(1, fresh.Attempt.AttemptNumber); Assert.Equal(state, Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, source.MessageId, CancellationToken)).State);
        }
    }

    [Fact]
    public async Task LateAckBeforeFreshPrepareRejectsCopyAndForeignContactOrChangedTextCannotReuseSource()
    {
        await using var f = await Fixture.CreateAsync(); var source = await LegacyResendSource(f, SendAttemptState.Unconfirmed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.PrepareAsync(f.Request(channel: true) with { PrivateResendSourceId = source.MessageId }, CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.PrepareAsync(f.Request() with { PrivateResendSourceId = source.MessageId, OriginalText = "different" }, CancellationToken));
        await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(new(f.NodeId, f.SessionId, 0x04030201, 10, DateTimeOffset.UtcNow), CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Storage.OutgoingMessages.PrepareAsync(f.Request() with { PrivateResendSourceId = source.MessageId }, CancellationToken));
        Assert.Single(await f.Storage.History.GetMessagesAsync(f.NodeId, f.ConversationId, null, 10, CancellationToken));
        Assert.Empty(await f.Storage.History.GetMessagesAsync(f.NodeId, f.Request(channel: true).ConversationId, null, 10, CancellationToken));
    }
}
