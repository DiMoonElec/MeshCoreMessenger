using MeshCoreMessenger.Desktop.Notifications;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    [Fact]
    public async Task NotificationClickOpensExactLocalMessageAndRejectsOtherNodeOrDeletedMessage()
    {
        await using var workspace = await Workspace.CreateAsync(publicMessageCount: 40);
        var root = workspace.Root;
        var router = new NotificationNavigationRouter(root, workspace.Storage.ConversationDirectory, workspace.Storage.History);
        var message = await workspace.StoreAsync(workspace.A, "notification target");
        root.Shell.SelectSection(ShellSection.PrivateChats);
        await router.OpenAsync(new MessageNotificationTarget(workspace.A.NodeId, workspace.A.ConversationId, message.MessageId), Token);
        Assert.Equal(ShellSection.PublicChats, root.Shell.SelectedItem.Section);
        Assert.Equal(workspace.A.ConversationId, root.SelectedConversation?.Id);
        Assert.Contains(root.Messages, item => item.Id == message.MessageId);
        var selected = root.SelectedConversation;
        await router.OpenAsync(new MessageNotificationTarget(workspace.B.NodeId, workspace.B.ConversationId, Guid.NewGuid()), Token);
        Assert.Same(selected, root.SelectedConversation);
        Assert.Contains("другой ноде", root.Status);
        await router.OpenAsync(new MessageNotificationTarget(workspace.A.NodeId, workspace.A.ConversationId, Guid.NewGuid()), Token);
        Assert.Contains("недоступно", root.Status);
        Assert.Equal(0, workspace.Supervisor.ConnectCalls);
    }
}
