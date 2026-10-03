using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.Presentation;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    [Theory]
    [InlineData(SendAttemptState.Delivered, MessageSendDisplayState.Delivered, "Доставлено")]
    [InlineData(SendAttemptState.Unconfirmed, MessageSendDisplayState.Unconfirmed, "Доставка не подтверждена")]
    [InlineData(SendAttemptState.Unknown, MessageSendDisplayState.Unknown, "Результат неизвестен")]
    public async Task PrivateSendUpdatesSameBubbleAfterSwitchingWorkspaceAndPreservesNewDraft(SendAttemptState terminal, MessageSendDisplayState display, string footer)
    {
        await using var w = await Workspace.CreateAsync(); await w.Root.StopAsync();
        var sender = new UiChannelSender(w);
        w.Root = w.CreateRoot(messageService: sender); await w.Root.LoadAsync(Token);
        await OnlineSender(w);
        w.Root.Shell.SelectSection(ShellSection.PrivateChats);
        var workspace = w.Root.Chats.Private; var composer = workspace.Composer;
        var history = workspace.Navigation.History;
        await history.JumpToLatestAsync(Token);
        var unread = history.UnreadCount; var originalCount = history.Messages.Count;
        composer.Text = "Личное сообщение 👋";
        await UntilAsync(() => composer.CanSend);
        Assert.Equal("36 / 160 байт", composer.ByteCounter);
        Assert.Contains("Отправить личное", composer.SendTooltip);
        await composer.SendCommand.ExecuteAsync(null);
        await UntilAsync(() => history.Messages.Any(m => m.Body == "Личное сообщение 👋" && m.Presentation.State == MessageSendDisplayState.AwaitingAck));
        var bubble = Assert.Single(history.Messages, m => m.Body == "Личное сообщение 👋");
        Assert.Equal("", composer.Text); Assert.False(bubble.RetryVisible);
        composer.Text = "следующий черновик";
        await UntilAsync(() => composer.CanSend);
        w.Root.Shell.SelectSection(ShellSection.PublicChats);
        w.Root.Chats.Public.Composer.Text = "публичный черновик";
        var attempt = Assert.Single(await w.Storage.OutgoingMessages.GetAttemptsAsync(w.A.NodeId, bubble.Id, Token));
        await w.Storage.OutgoingMessages.TransitionAsync(new(w.A.NodeId, bubble.Id, attempt.Id, attempt.SessionId!.Value,
            SendAttemptState.Accepted, terminal, DateTimeOffset.UtcNow, RoundTripMilliseconds: terminal == SendAttemptState.Delivered ? 10 : null), Token);
        await UntilAsync(() => bubble.Presentation.State == display);
        Assert.Same(bubble, history.Messages.Single(m => m.Id == bubble.Id));
        Assert.EndsWith(" • " + footer, bubble.MetadataText);
        Assert.Equal(originalCount + 1, history.Messages.Count);
        Assert.Equal(unread, history.UnreadCount);
        Assert.Equal(0, history.PendingNewMessageCount);
        Assert.Equal("следующий черновик", composer.Text);
        Assert.Equal("публичный черновик", w.Root.Chats.Public.Composer.Text);
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public async Task FirstPrivateSendToContactWithoutHistoryMaterializesAndReopensConversation()
    {
        await using var w = await Workspace.CreateAsync(); await w.Root.StopAsync();
        var logger = new SwitchLogger(); var sender = new UiChannelSender(w);
        var session = await OnlineSender(w);
        var key = Enumerable.Repeat((byte)22, 32).ToArray();
        await w.Storage.Directories.ApplySnapshotAsync(w.A.NodeId, session,
            [new(key, "New private peer", 1, 0, new byte[64], Now, 0, 0)],
            [new(0, "Shared name", w.A.Target.Identity, ChannelAccessKind.Unknown)], DateTimeOffset.UtcNow, Token);
        w.Root = w.CreateRoot(messageService: sender, logger: logger); await w.Root.LoadAsync(Token);
        var workspace = w.Root.Chats.Private;
        await workspace.Navigation.RefreshAsync(Token);
        var contact = workspace.Navigation.Conversations.Single(c => c.Title == "New private peer");
        Assert.Null(contact.Id);
        await workspace.Navigation.SelectConversationAsync(contact, Token);
        w.Root.Shell.SelectSection(ShellSection.PrivateChats);
        workspace.Composer.Text = "Первое сообщение";
        await UntilAsync(() => workspace.Composer.CanSend);
        await workspace.Composer.SendCommand.ExecuteAsync(null);
        await UntilAsync(() => workspace.Navigation.Messages.Any(m => m.Body == "Первое сообщение"));
        Assert.NotNull(workspace.SelectedConversation!.Id);
        Assert.Null(w.Root.ErrorMessage);
        Assert.Empty(logger.Errors);
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public async Task PrivateSendUsesSelectedContactAndOfflineDraftRemainsEditable()
    {
        await using var w = await Workspace.CreateAsync(); await w.Root.StopAsync();
        var sender = new UiChannelSender(w); w.Root = w.CreateRoot(messageService: sender);
        await w.Root.LoadAsync(Token);
        var composer = w.Root.Chats.Private.Composer;
        composer.Text = "offline private";
        Assert.False(composer.CanSend); Assert.True(composer.CanEdit);
        await OnlineSender(w);
        w.Root.Shell.SelectSection(ShellSection.PrivateChats);
        await w.Root.Chats.Private.Navigation.History.JumpToLatestAsync(Token);
        await UntilAsync(() => composer.CanSend);
        Assert.False(composer.HasSlotChoice);
        await composer.SendCommand.ExecuteAsync(null);
        await UntilAsync(() => w.Root.Chats.Private.Navigation.Messages.Any(m => m.Body == "offline private"));
        var message = w.Root.Chats.Private.Navigation.Messages.Single(m => m.Body == "offline private");
        var stored = await w.Storage.OutgoingMessages.GetAsync(w.A.NodeId, message.Id, Token);
        Assert.Equal(ConversationKind.Contact, stored.Recipient.Kind);
        Assert.Equal(w.A.PrivateTarget!.Identity, stored.Recipient.Identity.ToArray());
        Assert.Equal(1, sender.Calls);
    }
}
