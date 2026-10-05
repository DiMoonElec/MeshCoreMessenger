using Avalonia.Input;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.Presentation;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreMessenger.Desktop.Views.Chat;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    [Fact]
    public async Task SendButtonUsesCapturedDraftClearsOnlyTransferredRevisionAndUpdatesOneBubble()
    {
        await using var w = await Workspace.CreateAsync();
        await w.Root.StopAsync();
        var sender = new UiChannelSender(w);
        w.Root = w.CreateRoot(messageService: sender);
        await w.Root.LoadAsync(Token);
        await OnlineSender(w);
        var composer = w.Root.Chats.Public.Composer;
        composer.Text = "Тест 👋";
        await UntilAsync(() => composer.CanSend);
        var history = w.Root.Chats.Public.Navigation.History;
        await history.JumpToLatestAsync(Token);
        var old = history.Messages.ToArray();
        var privateDraft = w.Root.Chats.Private.Navigation.Draft;
        privateDraft.Text = "private unchanged";
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.Gate = gate.Task;
        var send = composer.SendCommand.ExecuteAsync(null);
        await UntilAsync(() => sender.Calls == 1 && composer.Text == "");
        Assert.False(composer.CanSend);
        await UntilAsync(() => history.Messages.Any(m => m.Body == "Тест 👋" && m.Presentation.State == MessageSendDisplayState.Sending));
        var sendingBubble = history.Messages.Single(m => m.Body == "Тест 👋");
        composer.SendCommand.Execute(null);
        composer.Text = "новый черновик";
        w.Root.Shell.SelectSection(ShellSection.Settings);
        gate.SetResult(); await send;
        await UntilAsync(() => history.Messages.Any(m => m.Body == "Тест 👋" && m.Presentation.State == MessageSendDisplayState.AcceptedByNode));
        var bubble = Assert.Single(history.Messages, m => m.Body == "Тест 👋");
        Assert.Same(sendingBubble, bubble);
        Assert.Equal(old.Length + 1, history.Messages.Count);
        Assert.Equal("новый черновик", composer.Text); Assert.Equal("private unchanged", privateDraft.Text);
        Assert.Equal(1, sender.Calls); Assert.True(bubble.RetryVisible);
        Assert.DoesNotContain("Доставлено", bubble.MetadataText);
        Assert.All(old, m => Assert.Contains(m, history.Messages));
    }

    [Fact]
    public async Task OutgoingWhileViewingOlderHistoryDoesNotResetViewportOrCountUnread()
    {
        await using var w = await Workspace.CreateAsync(200);
        var history = w.Root.Chats.Public.Navigation.History;
        await history.LoadOlderAsync(Token);
        history.ReportVisibleRange(history.Messages[1].LocalSequence, history.Messages[3].LocalSequence, true, false);
        var old = history.Messages.ToArray(); var unread = history.UnreadCount;
        var sender = new UiChannelSender(w); var session = await OnlineSender(w);
        var capture = new DraftCapture(w.A.Target, "outgoing", 1);
        w.Drafts.Update(w.A.Target, capture.Text, 1);
        var result = await sender.SendChannelAsync(new(w.A.NodeId, session, 1,
            new(ConversationKind.Channel, w.A.Target.Identity, w.A.Binding.Id, 0, w.A.Binding.Generation), capture, new()), cancellationToken: Token);
        await UntilAsync(() => history.CanLoadNewer);
        Assert.Equal(old, history.Messages.ToArray()); Assert.Equal(unread, history.UnreadCount);
        Assert.Equal(0, history.PendingNewMessageCount);
        await history.JumpToLatestAsync(Token);
        Assert.Equal(MessageSendDisplayState.AcceptedByNode, Assert.Single(history.Messages, m => m.Id == result.MessageId).Presentation.State);
    }

    [Fact]
    public async Task ExplicitSlotChoiceAndOfflineDraftKeepSendAdmissionHonest()
    {
        await using var w = await Workspace.CreateAsync(); await w.Root.StopAsync();
        var sender = new UiChannelSender(w);
        w.Root = w.CreateRoot(messageService: sender); await w.Root.LoadAsync(Token);
        var composer = w.Root.Chats.Public.Composer;
        composer.Text = "offline draft"; Assert.False(composer.CanSend);
        await OnlineSender(w, multipleSlots: true);
        await UntilAsync(() => composer.HasSlotChoice);
        Assert.Null(composer.SelectedSlot); Assert.False(composer.CanSend);
        composer.SelectedSlot = 0;
        await UntilAsync(() => composer.CanSend);
        await composer.SendCommand.ExecuteAsync(null);
        Assert.Equal((byte)0, sender.LastSlot); Assert.Equal(1, sender.Calls);
    }

    [Theory]
    [InlineData(Key.Enter, KeyModifiers.None, false, true)]
    [InlineData(Key.Enter, KeyModifiers.Shift, false, false)]
    [InlineData(Key.Enter, KeyModifiers.None, true, false)]
    [InlineData(Key.Enter, KeyModifiers.Control, false, false)]
    [InlineData(Key.A, KeyModifiers.None, false, false)]
    public void EnterGesturePreservesNewlinesAndIme(Key key, KeyModifiers modifiers, bool composing, bool send) =>
        Assert.Equal(send, ComposerView.IsSendGesture(key, modifiers, composing));

    private static async Task<Guid> OnlineSender(Workspace w, bool multipleSlots = false)
    {
        var session = Guid.NewGuid();
        await w.Storage.Sessions.CreateAsync(new(session, w.ProfileId, w.A.NodeId, DateTimeOffset.UtcNow, null, null), Token);
        if (multipleSlots)
            await w.Storage.Directories.ApplySnapshotAsync(w.A.NodeId, session,
                [new(Enumerable.Repeat((byte)9, 32).ToArray(), "Personal chat", 1, 0, new byte[64], Now, 0, 0)],
                [new(0, "Shared name", w.A.Target.Identity, ChannelAccessKind.Unknown), new(1, "Shared name", w.A.Target.Identity, ChannelAccessKind.Unknown)],
                DateTimeOffset.UtcNow, Token);
        w.Supervisor.Publish(new(ConnectionSupervisorState.Online, 1, w.ProfileId, session, w.A.NodeId, null, null) { SenderName = "Emulated" });
        return session;
    }

    // UI adapter deliberately uses the real outgoing store/notifications. Core tests exercise the production service/session.
    private sealed class UiChannelSender(Workspace w) : IMessageService
    {
        public async Task<ChannelSendOutcome> SendChannelAsNewAsync(ChannelRepeatRequest request, CancellationToken cancellationToken = default)
        {
            var stored = await w.Storage.OutgoingMessages.GetAsync(request.NodeId, request.MessageId, cancellationToken);
            Calls++; LastSlot = request.Recipient.Slot;
            var prepared = await w.Storage.OutgoingMessages.PrepareAsync(new(Guid.NewGuid(), request.NodeId, request.SessionId,
                stored.ConversationId, request.Recipient, stored.OriginalText, stored.TransmissionText, 160, DateTimeOffset.UtcNow), cancellationToken);
            await w.Storage.OutgoingMessages.TransitionAsync(new(request.NodeId, prepared.MessageId, prepared.Attempt.Id, request.SessionId,
                SendAttemptState.Prepared, SendAttemptState.Sending, DateTimeOffset.UtcNow), cancellationToken);
            if (Gate is not null) await Gate.WaitAsync(cancellationToken);
            await w.Storage.OutgoingMessages.TransitionAsync(new(request.NodeId, prepared.MessageId, prepared.Attempt.Id, request.SessionId,
                SendAttemptState.Sending, SendAttemptState.Accepted, DateTimeOffset.UtcNow, AckExpectation.NotExpected), cancellationToken);
            return new(prepared.MessageId, SendAttemptState.Accepted);
        }
        public async Task<ChannelSendOutcome> RepeatChannelAsync(ChannelRepeatRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; LastSlot = request.Recipient.Slot;
            var prepared = await w.Storage.OutgoingMessages.PrepareChannelRepeatAsync(new(request.NodeId, request.MessageId,
                request.SessionId, request.Recipient, request.ExpectedAttemptNumber, DateTimeOffset.UtcNow), cancellationToken);
            await w.Storage.OutgoingMessages.TransitionAsync(new(request.NodeId, request.MessageId, prepared.Attempt.Id, request.SessionId,
                SendAttemptState.Prepared, SendAttemptState.Sending, DateTimeOffset.UtcNow), cancellationToken);
            if (Gate is not null) await Gate.WaitAsync(cancellationToken);
            await w.Storage.OutgoingMessages.TransitionAsync(new(request.NodeId, request.MessageId, prepared.Attempt.Id, request.SessionId,
                SendAttemptState.Sending, SendAttemptState.Accepted, DateTimeOffset.UtcNow, AckExpectation.NotExpected), cancellationToken);
            return new(request.MessageId, SendAttemptState.Accepted);
        }
        public Task? Gate { get; set; }
        public int Calls { get; private set; }
        public byte? LastSlot { get; private set; }
        public async Task<IReadOnlyList<OutgoingRecipient>> GetChannelTargetsAsync(Guid nodeId, ReadOnlyMemory<byte> fingerprint, CancellationToken cancellationToken = default)
        {
            var details = await w.Storage.ConversationDirectory.GetChannelDetailsAsync(nodeId, fingerprint, cancellationToken);
            var targets = new List<OutgoingRecipient>();
            foreach (var slot in details?.ActiveSlots ?? [])
            {
                var binding = (await w.Storage.Directories.GetActiveChannelBindingAsync(nodeId, slot, cancellationToken))!;
                targets.Add(new(ConversationKind.Channel, fingerprint.ToArray(), binding.Id, slot, binding.Generation));
            }
            return targets;
        }
        public async Task<PrivateSendOutcome> SendPrivateAsync(PrivateSendRequest request, Func<DraftCapture, Task>? transferred = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            await w.Drafts.FlushAsync(request.Draft.Target, cancellationToken);
            var draft = await w.Storage.Drafts.GetAsync(request.Draft.Target, cancellationToken);
            var prepared = await w.Storage.OutgoingMessages.PrepareAsync(new(Guid.NewGuid(), request.NodeId, request.SessionId,
                request.Draft.Target.ConversationId ?? draft!.ConversationId, request.Recipient, request.Draft.Text, request.Draft.Text, 160, DateTimeOffset.UtcNow), cancellationToken);
            if (await w.Drafts.ClearTransferredAsync(request.Draft, cancellationToken) && transferred is not null) await transferred(request.Draft);
            await w.Storage.OutgoingMessages.TransitionAsync(new(request.NodeId, prepared.MessageId, prepared.Attempt.Id, request.SessionId,
                SendAttemptState.Prepared, SendAttemptState.Sending, DateTimeOffset.UtcNow), cancellationToken);
            await w.Storage.OutgoingMessages.TransitionAsync(new(request.NodeId, prepared.MessageId, prepared.Attempt.Id, request.SessionId,
                SendAttemptState.Sending, SendAttemptState.Accepted, DateTimeOffset.UtcNow, AckExpectation.Expected, 42, new byte[] { 1, 0, 0, 0 }), cancellationToken);
            return new(prepared.MessageId, SendAttemptState.Accepted);
        }
        public async Task<PrivateSendOutcome> SendPrivateAsNewAsync(PrivateResendRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            var source = await w.Storage.OutgoingMessages.GetAsync(request.NodeId, request.MessageId, cancellationToken);
            if (Gate is not null) await Gate.WaitAsync(cancellationToken);
            var prepared = await w.Storage.OutgoingMessages.PrepareAsync(new(Guid.NewGuid(), request.NodeId, request.SessionId,
                source.ConversationId, source.Recipient, source.OriginalText, source.OriginalText, 160, DateTimeOffset.UtcNow,
                source.MessageId), cancellationToken);
            await w.Storage.OutgoingMessages.TransitionAsync(new(request.NodeId, prepared.MessageId, prepared.Attempt.Id, request.SessionId,
                SendAttemptState.Prepared, SendAttemptState.Sending, DateTimeOffset.UtcNow), cancellationToken);
            await w.Storage.OutgoingMessages.TransitionAsync(new(request.NodeId, prepared.MessageId, prepared.Attempt.Id, request.SessionId,
                SendAttemptState.Sending, SendAttemptState.Accepted, DateTimeOffset.UtcNow, AckExpectation.Expected, 43,
                new byte[] { 2, 0, 0, 0 }), cancellationToken);
            return new(prepared.MessageId, SendAttemptState.Accepted);
        }
        public async Task<ChannelSendOutcome> SendChannelAsync(ChannelSendRequest request, Func<DraftCapture, Task>? transferred = null, CancellationToken cancellationToken = default)
        {
            Calls++; LastSlot = request.Recipient.Slot;
            var prepared = await w.Storage.OutgoingMessages.PrepareAsync(new(Guid.NewGuid(), request.NodeId, request.SessionId,
                request.Draft.Target.ConversationId!.Value, request.Recipient, request.Draft.Text, request.Draft.Text, 150, DateTimeOffset.UtcNow), cancellationToken);
            if (await w.Drafts.ClearTransferredAsync(request.Draft, cancellationToken) && transferred is not null) await transferred(request.Draft);
            await w.Storage.OutgoingMessages.TransitionAsync(new(request.NodeId, prepared.MessageId, prepared.Attempt.Id, request.SessionId,
                SendAttemptState.Prepared, SendAttemptState.Sending, DateTimeOffset.UtcNow), cancellationToken);
            if (Gate is not null) await Gate.WaitAsync(cancellationToken);
            await w.Storage.OutgoingMessages.TransitionAsync(new(request.NodeId, prepared.MessageId, prepared.Attempt.Id, request.SessionId,
                SendAttemptState.Sending, SendAttemptState.Accepted, DateTimeOffset.UtcNow, AckExpectation.NotExpected), cancellationToken);
            return new(prepared.MessageId, SendAttemptState.Accepted);
        }
    }
}
