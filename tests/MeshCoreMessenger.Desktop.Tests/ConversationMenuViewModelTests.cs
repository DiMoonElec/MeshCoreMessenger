using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class ConversationMenuViewModelTests
{
    [Fact]
    public void OnlyPrivateMenuExposesRoutePlaceholders()
    {
        var clear = new HistoryClearViewModel(() => null, null, _ => Task.CompletedTask);
        var publicMenu = new ConversationMenuViewModel(MessengerNavigationTab.Channels, clear);
        var privateMenu = new ConversationMenuViewModel(MessengerNavigationTab.Personal, clear);
        Assert.DoesNotContain(publicMenu.Items, i => i.Action is ConversationMenuAction.ResetRoute or ConversationMenuAction.SetRoute);
        Assert.Contains(privateMenu.Items, i => i.Action == ConversationMenuAction.ResetRoute);
        Assert.Contains(privateMenu.Items, i => i.Action == ConversationMenuAction.SetRoute);
        Assert.Contains(publicMenu.Items, i => i.Action == ConversationMenuAction.ClearHistory);
        Assert.Contains(privateMenu.Items, i => i.Action == ConversationMenuAction.ClearHistory);
    }

    [Fact]
    public async Task StableCommandCapturesCurrentTargetAndOnlyRequestsConfirmation()
    {
        var selected = Target();
        var service = new Service();
        var clear = new HistoryClearViewModel(() => selected, service, _ => Task.CompletedTask);
        var menu = new ConversationMenuViewModel(MessengerNavigationTab.Personal, clear);
        var items = menu.Items;
        var command = items.Single(i => i.Action == ConversationMenuAction.ClearHistory).Command;
        HistoryClearRequestedEventArgs? request = null;
        menu.HistoryClearRequested += (_, args) => request = args;
        Assert.False(command.CanExecute(null));
        await menu.RefreshAsync();
        var oldTarget = selected;
        selected = Target();
        command.Execute(null); // Old availability must not confirm the old recipient.
        Assert.Null(request);
        await menu.RefreshAsync();
        command.Execute(null);
        Assert.Equal(selected, request!.Target);
        Assert.NotEqual(oldTarget, request.Target);
        Assert.Same(clear, request.Operation);
        Assert.Same(items, menu.Items);
        Assert.Same(command, menu.Items.Single(i => i.Action == ConversationMenuAction.ClearHistory).Command);
        Assert.Equal(0, service.Clears);
        await request.Operation.ClearAsync(request.Target);
        Assert.Equal(1, service.Clears);
    }

    [Fact]
    public async Task WiringTracksAvailabilityAndReleasesReplacedOperation()
    {
        var target = Target(); var service = new Service();
        var old = new HistoryClearViewModel(() => target, service, _ => Task.CompletedTask);
        var menu = new ConversationMenuViewModel(MessengerNavigationTab.Channels, old);
        var item = menu.Items.Single(i => i.Action == ConversationMenuAction.ClearHistory);
        var notifications = 0;
        item.Command.CanExecuteChanged += (_, _) => notifications++;
        await menu.RefreshAsync();
        Assert.True(item.Command.CanExecute(null));
        service.Reason = "ACK pending";
        await menu.RefreshAsync();
        Assert.False(item.Command.CanExecute(null));
        Assert.Equal("ACK pending", item.AvailabilityMessage);
        Assert.True(notifications > 0);
        var replacement = new HistoryClearViewModel(() => null, null, _ => Task.CompletedTask);
        menu.SetHistoryClear(replacement);
        notifications = 0;
        service.Reason = null;
        await old.RefreshAsync();
        Assert.Equal(0, notifications);
        Assert.False(item.Command.CanExecute(null));
    }

    private static HistoryClearTarget Target() => new(Guid.NewGuid(), Guid.NewGuid(), "Node", "Chat");
    private sealed class Service : IHistoryClearService
    {
        public string? Reason { get; set; }
        public int Clears { get; private set; }
        public Task<string?> GetUnavailableReasonAsync(Guid nodeId, Guid conversationId, CancellationToken cancellationToken = default) => Task.FromResult(Reason);
        public Task<HistoryClearResult> ClearAsync(Guid nodeId, Guid conversationId, CancellationToken cancellationToken = default)
        {
            Clears++;
            return Task.FromResult(new HistoryClearResult(nodeId, conversationId, 42, 2));
        }
    }
}
