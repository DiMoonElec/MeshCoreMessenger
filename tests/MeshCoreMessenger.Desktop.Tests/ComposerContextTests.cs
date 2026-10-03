using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    [Fact]
    public async Task ComposerContextsFollowRealSessionNameAndKeepPublicPrivateOfflineDraftsIndependent()
    {
        await using var workspace = await Workspace.CreateAsync();
        var root = workspace.Root;
        var channel = root.Chats.Public.Composer;
        var personal = root.Chats.Private.Composer;
        channel.Text = new string('я', 80);
        personal.Text = "я👋";
        Assert.Equal("160 / — байт", channel.ByteCounter);
        Assert.Equal("6 / 160 байт", personal.ByteCounter);
        workspace.Supervisor.Publish(new(ConnectionSupervisorState.Online, 1, workspace.ProfileId,
            workspace.A.SessionId, workspace.A.NodeId, null, null) { SenderName = "🐈" });
        await UntilAsync(() => channel.Readiness == SendReadiness.Ready && personal.Readiness == SendReadiness.Ready);
        Assert.Equal("160 / 154 байт", channel.ByteCounter);
        Assert.Contains("Превышен лимит на 6 байт", channel.StatusLine);
        Assert.True(personal.IsReadyForSend);
        Assert.False(channel.CanSend);
        Assert.False(personal.CanSend);
        workspace.Supervisor.Publish(workspace.Supervisor.Snapshot with { Generation = 2, SessionId = Guid.NewGuid(), SenderName = "A" });
        await UntilAsync(() => channel.Context.SenderName == "A");
        Assert.Equal("160 / 157 байт", channel.ByteCounter);
        workspace.Supervisor.Publish(workspace.Supervisor.Snapshot with { State = ConnectionSupervisorState.Offline, SessionId = null, NodeId = null, SenderName = null });
        await UntilAsync(() => channel.Readiness == SendReadiness.Offline);
        Assert.Equal("160 / — байт", channel.ByteCounter);
        Assert.Equal(new string('я', 80), channel.Text);
        Assert.Equal("я👋", personal.Text);
        Assert.Equal(0, workspace.Supervisor.ConnectCalls);
    }

    [Fact]
    public async Task OldReadinessReadCannotOverwriteOfflineContextAndShutdownCancelsOwnedReads()
    {
        await using var workspace = await Workspace.CreateAsync();
        await workspace.Root.StopAsync();
        var reader = new GatedReadiness();
        var root = workspace.CreateRoot(sendReadiness: reader);
        await root.LoadAsync(Token);
        workspace.Supervisor.Publish(new(ConnectionSupervisorState.Online, 1, workspace.ProfileId,
            workspace.A.SessionId, workspace.A.NodeId, null, null) { SenderName = "🐈" });
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        workspace.Supervisor.Publish(workspace.Supervisor.Snapshot with { State = ConnectionSupervisorState.Offline, NodeId = null, SessionId = null, SenderName = null });
        await UntilAsync(() => root.Chats.Public.Composer.Readiness == SendReadiness.Offline);
        reader.Completion.TrySetResult(SendReadiness.Ready);
        await Task.Delay(50, Token);
        Assert.Equal(SendReadiness.Offline, root.Chats.Public.Composer.Readiness);
        Assert.Null(root.Chats.Public.Composer.Context.SenderName);
        reader.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.Supervisor.Publish(new(ConnectionSupervisorState.Online, 2, workspace.ProfileId,
            workspace.A.SessionId, workspace.A.NodeId, null, null) { SenderName = "A" });
        await UntilAsync(() => root.Chats.Public.Composer.Readiness == SendReadiness.Checking);
        await root.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(reader.Cancellations > 0);
    }

    private sealed class GatedReadiness : ISendReadinessReader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SendReadiness> Completion { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Cancellations;
        public async Task<SendReadiness> ReadAsync(ConnectionSupervisorSnapshot connection, Guid? node,
            SendRecipient? recipient, CancellationToken token = default)
        {
            if (SendReadinessReader.CheckContext(connection, node, recipient) is { } unavailable) return unavailable;
            Started.TrySetResult();
            try { return await Completion.Task.WaitAsync(token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref Cancellations); throw; }
        }
    }
}
