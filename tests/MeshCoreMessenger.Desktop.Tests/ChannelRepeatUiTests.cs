using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreMessenger.Desktop.Presentation;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    [Fact]
    public async Task ChannelRepeatOfflineStartupDoesNotTouchUiDispatcherBeforeAvaloniaStarts()
    {
        await using var w = await Workspace.CreateAsync();
        await w.Root.StopAsync();
        var dispatcher = new StartupRepeatDispatcher();
        w.Root = w.CreateRoot(dispatcher: dispatcher, messageService: new UiChannelSender(w));
        await w.Root.LoadAsync(Token);
        Assert.Null(w.Root.ErrorMessage);
        Assert.NotEmpty(w.Root.Chats.Public.Navigation.Messages);
        Assert.Equal(0, dispatcher.Calls);
    }

    private sealed class StartupRepeatDispatcher : MeshCoreMessenger.Desktop.Lifecycle.IUiDispatcher
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            action();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ChannelRepeatKeepsOneBubbleAndDraftAndBlocksDoubleClickOfflineAndIncoming()
    {
        await using var w = await Workspace.CreateAsync(); await w.Root.StopAsync();
        var sender = new UiChannelSender(w); w.Root = w.CreateRoot(messageService: sender); await w.Root.LoadAsync(Token);
        await OnlineSender(w);
        var workspace = w.Root.Chats.Public; var composer = workspace.Composer;
        await workspace.Navigation.History.JumpToLatestAsync(Token);
        composer.Text = "Повторить этот текст"; await UntilAsync(() => composer.CanSend);
        await composer.SendCommand.ExecuteAsync(null);
        await UntilAsync(() => workspace.Navigation.Messages.Any(m => m.Body == "Повторить этот текст" && m.CanRetry));
        var bubble = workspace.Navigation.Messages.Single(m => m.Body == "Повторить этот текст");
        var count = workspace.Navigation.Messages.Count;
        composer.Text = "Черновик оставить";
        Assert.False(bubble.RetryRequiresConfirmation);
        Assert.DoesNotContain(workspace.Navigation.Messages.Where(m => !m.IsOutgoing), m => m.RetryVisible);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); sender.Gate = release.Task;
        await UntilAsync(() => bubble.CanRetry);
        var command = (CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)bubble.RetryCommand!;
        bubble.RequestRetry();
        Assert.NotNull(command.ExecutionTask);
        await UntilAsync(() => sender.Calls == 2 || command.ExecutionTask.IsCompleted);
        Assert.True(sender.Calls == 2, $"{composer.StatusLine}; retry={bubble.CanRetry}, state={bubble.Presentation.State}, calls={sender.Calls}, task={command.ExecutionTask.Status}");
        Assert.False(bubble.CanRetry); bubble.RequestRetry(); Assert.Equal(2, sender.Calls);
        release.SetResult();
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)bubble.RetryCommand!).ExecutionTask!;
        await UntilAsync(() => bubble.Presentation.AttemptNumber == 2 && bubble.Presentation.State == MessageSendDisplayState.AcceptedByNode);
        Assert.Equal(count, workspace.Navigation.Messages.Count);
        Assert.Same(bubble, workspace.Navigation.Messages.Single(m => m.Id == bubble.Id));
        Assert.Equal("Черновик оставить", composer.Text);
        Assert.Equal(2, (await w.Storage.OutgoingMessages.GetAttemptsAsync(w.A.NodeId, bubble.Id, Token)).Count);
        Assert.Equal("Повторить доставку", bubble.RetryLabel);
        sender.Gate = null;
        await UntilAsync(() => bubble.CanSendAsNew);
        bubble.RequestSendAsNew();
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)bubble.SendAsNewCommand!).ExecutionTask!;
        await UntilAsync(() => workspace.Navigation.Messages.Count(m => m.Body == bubble.Body) == 2);
        var copy = workspace.Navigation.Messages.Single(m => m.Body == bubble.Body && m.Id != bubble.Id);
        Assert.Equal(count + 1, workspace.Navigation.Messages.Count);
        Assert.Single(await w.Storage.OutgoingMessages.GetAttemptsAsync(w.A.NodeId, copy.Id, Token));
        Assert.Equal(2, (await w.Storage.OutgoingMessages.GetAttemptsAsync(w.A.NodeId, bubble.Id, Token)).Count);
        Assert.Equal("Черновик оставить", composer.Text);
        w.Supervisor.Publish(w.Supervisor.Snapshot with { State = MeshCoreMessenger.Core.Domain.ConnectionSupervisorState.Offline });
        await UntilAsync(() => !bubble.CanRetry);
        Assert.False(bubble.CanSendAsNew);
        bubble.RequestRetry(); bubble.RequestSendAsNew(); Assert.Equal(3, sender.Calls);
    }
}
