using CommunityToolkit.Mvvm.Input;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    [Fact]
    public async Task PrivateResendMenuCreatesNewBubblePreservesDraftAndCapturesTargetAcrossWorkspaceSwitch()
    {
        await using var w = await Workspace.CreateAsync(); await w.Root.StopAsync();
        var sender = new UiChannelSender(w); w.Root = w.CreateRoot(messageService: sender); await w.Root.LoadAsync(Token);
        await OnlineSender(w); w.Root.Shell.SelectSection(ShellSection.PrivateChats);
        var workspace = w.Root.Chats.Private; var history = workspace.Navigation.History;
        await history.JumpToLatestAsync(Token);
        workspace.Composer.Text = "Ручной повтор 👋"; await UntilAsync(() => workspace.Composer.CanSend);
        await workspace.Composer.SendCommand.ExecuteAsync(null);
        await UntilAsync(() => history.Messages.Any(m => m.Body == "Ручной повтор 👋"));
        var source = history.Messages.Single(m => m.Body == "Ручной повтор 👋");
        Assert.False(source.SendAsNewVisible);
        var attempt = Assert.Single(await w.Storage.OutgoingMessages.GetAttemptsAsync(w.A.NodeId, source.Id, Token));
        await w.Storage.OutgoingMessages.TransitionAsync(new(w.A.NodeId, source.Id, attempt.Id, attempt.SessionId!.Value,
            SendAttemptState.Accepted, SendAttemptState.Unconfirmed, DateTimeOffset.UtcNow), Token);
        await UntilAsync(() => source.CanSendAsNew);
        Assert.Equal("Отправить еще раз", source.SendAsNewLabel); Assert.False(source.RetryVisible);
        Assert.DoesNotContain(history.Messages, m => !m.IsOutgoing && m.SendAsNewVisible);
        var count = history.Messages.Count;
        workspace.Composer.Text = "Черновик оставить";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); sender.Gate = release.Task;
        source.RequestSendAsNew(); var command = (IAsyncRelayCommand)source.SendAsNewCommand!;
        var task = command.ExecutionTask!;
        await UntilAsync(() => sender.Calls == 2);
        Assert.False(source.CanSendAsNew); source.RequestSendAsNew(); Assert.Equal(2, sender.Calls);
        w.Root.Shell.SelectSection(ShellSection.PublicChats); w.Root.Chats.Public.Composer.Text = "Другой черновик";
        release.SetResult(); await task;
        Assert.DoesNotContain("Не удалось", workspace.Composer.StatusLine);
        Assert.Null(w.Root.ErrorMessage);
        Assert.Equal(2, (await w.Storage.History.GetMessagesAsync(w.A.NodeId, source.ConversationId, null, 20, Token)).Count(m => m.Text == source.Body));
        w.Root.Shell.SelectSection(ShellSection.PrivateChats);
        await history.JumpToLatestAsync(Token);
        await UntilAsync(() => history.Messages.Count(m => m.Body == source.Body) == 2);
        source = history.Messages.Single(m => m.Id == source.Id);
        var fresh = history.Messages.Single(m => m.Body == source.Body && m.Id != source.Id);
        Assert.Equal(count + 1, history.Messages.Count); Assert.Equal(source.ConversationId, fresh.ConversationId);
        Assert.False(fresh.SendAsNewVisible); Assert.Single(await w.Storage.OutgoingMessages.GetAttemptsAsync(w.A.NodeId, source.Id, Token));
        Assert.Equal("Черновик оставить", workspace.Composer.Text); Assert.Equal("Другой черновик", w.Root.Chats.Public.Composer.Text);
        await w.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(new(w.A.NodeId, attempt.SessionId.Value, 1, 10, DateTimeOffset.UtcNow), Token);
        await UntilAsync(() => !source.SendAsNewVisible);
        Assert.False(source.CanSendAsNew); Assert.Equal(2, sender.Calls);
        Assert.Equal(SendAttemptState.Accepted, Assert.Single(await w.Storage.OutgoingMessages.GetAttemptsAsync(w.A.NodeId, fresh.Id, Token)).State);
        w.Supervisor.Publish(w.Supervisor.Snapshot with { State = ConnectionSupervisorState.Offline });
        Assert.False(source.CanSendAsNew);
    }
}
