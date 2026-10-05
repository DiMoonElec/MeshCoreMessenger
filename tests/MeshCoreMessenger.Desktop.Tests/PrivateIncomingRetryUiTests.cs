using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    [Fact]
    public async Task PrivateRetryCommitDoesNotAddBubblePendingOrUnreadButNewTimestampDoes()
    {
        await using var w = await Workspace.CreateAsync();
        var history = w.Root.Chats.Private.Navigation.History;
        var before = await w.Storage.ReadStates.GetAsync(w.A.NodeId, w.A.PrivateConversationId, Token);
        var message = new ContactMessage(Enumerable.Repeat((byte)9, 6).ToArray(), 1, MessageTextType.Plain,
            Now, "Повтор ЛС", ReadOnlyMemory<byte>.Empty, 0);
        var envelope = new IncomingMessageEnvelope(Guid.NewGuid(), w.A.SessionId, w.A.NodeId, message, Now, null, null);
        var first = await w.Storage.IncomingMessages.StoreAsync(envelope, Token);
        w.Notifications.Publish(first);
        await UntilAsync(() => history.PendingNewMessageCount == 1);
        var retry = await w.Storage.IncomingMessages.StoreAsync(envelope with { EventId = Guid.NewGuid(),
            Message = message with { PathLength = 4, SnrDb = 12 } }, Token);
        w.Notifications.Publish(retry);
        await history.HandleCommittedMessageAsync(retry, Token);
        Assert.False(retry.Inserted); Assert.Equal(first.MessageId, retry.MessageId);
        Assert.Equal(1, history.PendingNewMessageCount);
        var state = await w.Storage.ReadStates.GetAsync(w.A.NodeId, w.A.PrivateConversationId, Token);
        Assert.Equal(before.UnreadCount + 1, state.UnreadCount); Assert.Equal(before.LastReadSequence, state.LastReadSequence);
        var fresh = await w.Storage.IncomingMessages.StoreAsync(envelope with { EventId = Guid.NewGuid(),
            Message = message with { Timestamp = Now.AddSeconds(1) } }, Token);
        w.Notifications.Publish(fresh);
        await UntilAsync(() => history.PendingNewMessageCount == 2);
        Assert.Equal(before.UnreadCount + 2, (await w.Storage.ReadStates.GetAsync(w.A.NodeId, w.A.PrivateConversationId, Token)).UnreadCount);
        await history.JumpToLatestAsync(Token);
        Assert.Equal(2, history.Messages.Count(m => m.Body == "Повтор ЛС"));
        Assert.Equal(2, history.Messages.Where(m => m.Body == "Повтор ЛС").Select(m => m.Id).Distinct().Count());
    }
}
