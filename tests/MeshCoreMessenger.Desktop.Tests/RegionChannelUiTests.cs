using CommunityToolkit.Mvvm.Input;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    [Fact]
    public async Task RegionCardFollowsSettingsVisitAndInUseDeleteKeepsAssignmentAndReportsError()
    {
        await using var f = await Workspace.CreateAsync(); await f.Root.StopAsync();
        var root = f.CreateRoot(includeRegions: true);
        try
        {
            await root.LoadAsync(Token);
            var service = new RegionService(f.Storage.Regions, TimeProvider.System);
            var region = await service.CreatePublicAsync(f.A.NodeId, "Area", "Area", Token);
            var selected = root.Chats.Public.SelectedConversation!;
            var channel = (await f.Storage.ConversationDirectory.GetChannelDetailsAsync(f.A.NodeId, selected.Entry.Identity, Token))!;
            await service.AssignChannelAsync(f.A.NodeId, channel.Id, FloodScopeMode.ExplicitRegion, region.Id, 0, Token);
            Assert.Empty(root.Regions.CatalogSearch.Rows);
            root.Shell.SelectSection(ShellSection.Settings); await root.Regions.CatalogSearch.RefreshAsync();
            var row = Assert.Single(root.Regions.CatalogSearch.Rows); Assert.Equal("−", row.Symbol);
            await row.ToggleCommand.ExecuteAsync(null); Assert.Equal("−", row.Symbol); Assert.NotNull(root.ErrorMessage);
            Assert.Equal(region.Id, (await service.GetChannelPreferenceAsync(f.A.NodeId, channel.Id, Token)).RegionId);
            await service.AssignChannelAsync(f.A.NodeId, channel.Id, FloodScopeMode.InheritDefault, null, 1, Token);
            await row.ToggleCommand.ExecuteAsync(null); Assert.Equal("+", row.Symbol);
            root.Shell.SelectSection(ShellSection.PublicChats); Assert.Empty(root.Regions.CatalogSearch.Rows);
            root.Shell.SelectSection(ShellSection.Settings); await root.Regions.CatalogSearch.RefreshAsync();
            Assert.Empty(root.Regions.CatalogSearch.Rows); Assert.Equal(0, f.Supervisor.ConnectCalls);
        }
        finally { await root.StopAsync(); }
    }

    [Fact]
    public async Task RegionPolicyChangesRefreshComposerBudgetWithoutTrimmingOrSendingDraft()
    {
        await using var f = await Workspace.CreateAsync(); await f.Root.StopAsync();
        var sender = new UiChannelSender(f);
        var root = f.CreateRoot(sendReadiness: new RegionBudgetReader(f), messageService: sender, includeRegions: true);
        try
        {
            await root.LoadAsync(Token);
            f.Supervisor.Publish(new(ConnectionSupervisorState.Online, 1, f.ProfileId, f.A.SessionId, f.A.NodeId, null, null) { SenderName = "A" });
            var composer = root.Chats.Public.Composer; composer.Text = new string('я', 30);
            await UntilAsync(() => composer.ByteCounter == "60 / 100 байт" && composer.CanSend);
            var service = new RegionService(f.Storage.Regions, TimeProvider.System);
            var region = await service.CreatePublicAsync(f.A.NodeId, "Area", "Area", Token);
            var selected = root.Chats.Public.SelectedConversation!;
            var channel = (await f.Storage.ConversationDirectory.GetChannelDetailsAsync(f.A.NodeId, selected.Entry.Identity, Token))!;
            await service.AssignChannelAsync(f.A.NodeId, channel.Id, MeshCoreSharp.Models.FloodScopeMode.ExplicitRegion, region.Id, 0, Token);
            await UntilAsync(() => composer.ByteCounter == "60 / 50 байт" && !composer.CanSend);
            Assert.Equal(new string('я', 30), composer.Text); Assert.Equal(0, sender.Calls);
            await service.AssignChannelAsync(f.A.NodeId, channel.Id, MeshCoreSharp.Models.FloodScopeMode.ForceUnscoped, null, 1, Token);
            await UntilAsync(() => composer.ByteCounter == "60 / 100 байт" && composer.CanSend);
        }
        finally { await root.StopAsync(); }
    }
    private sealed class RegionBudgetReader(Workspace workspace) : ISendReadinessReader
    {
        private readonly SendReadinessReader _inner = new(workspace.Storage.Directories, workspace.Storage.ConversationDirectory);
        public Task<SendReadiness> ReadAsync(ConnectionSupervisorSnapshot connection, Guid? node, SendRecipient? recipient, CancellationToken token = default) =>
            _inner.ReadAsync(connection, node, recipient, token);
        public async Task<SendBudgetReadiness> ReadBudgetAsync(ConnectionSupervisorSnapshot connection, Guid? node, SendRecipient? recipient, CancellationToken token = default)
        {
            var readiness = await ReadAsync(connection, node, recipient, token);
            if (readiness != SendReadiness.Ready || recipient!.Kind != ConversationKind.Channel) return new(readiness, null);
            var channel = (await workspace.Storage.ConversationDirectory.GetChannelDetailsAsync(node!.Value, recipient.Identity, token))!;
            var preference = await workspace.Storage.Regions.GetChannelPreferenceAsync(node.Value, channel.Id, token);
            return new(SendReadiness.Ready, new(node.Value, connection.SessionId, connection.Generation,
                new(ConversationKind.Channel, recipient.Identity), null, preference.Mode == MeshCoreSharp.Models.FloodScopeMode.ExplicitRegion ? 50 : 100,
                RegionSnapshotFreshness.CurrentSession, 0, connection.SenderName));
        }
    }
    [Fact]
    public async Task RegionMenuOfflinePolicyUpdatesHeaderWithoutChangingHistoryOrDraft()
    {
        await using var f = await Workspace.CreateAsync(); await f.Root.StopAsync();
        var root = f.CreateRoot(includeRegions: true);
        try
        {
            await root.LoadAsync(Token);
            var regionService = new RegionService(f.Storage.Regions, TimeProvider.System);
            await regionService.CreatePublicAsync(f.A.NodeId, "Ростов", "ru-ros", Token);
            var region = root.Chats.Public.Regions!;
            await region.RefreshAsync();
            var selected = root.Chats.Public.SelectedConversation!;
            var history = root.Chats.Public.Navigation.Messages.ToArray();
            root.Chats.Public.Navigation.Draft.Text = "Не терять черновик";
            await UntilAsync(() => region.Choices.Any(c => c.Header.Contains("ru-ros")));
            var choice = region.Choices.Single(c => c.Header.Contains("ru-ros"));
            await ((IAsyncRelayCommand)choice.Command).ExecuteAsync(null);
            await region.RefreshAsync();
            await UntilAsync(() => region.Text == "Регион: ru-ros");
            var details = (await f.Storage.ConversationDirectory.GetChannelDetailsAsync(f.A.NodeId, selected.Entry.Identity, Token))!;
            Assert.Equal(MeshCoreSharp.Models.FloodScopeMode.ExplicitRegion, (await f.Storage.Regions.GetChannelPreferenceAsync(f.A.NodeId, details.Id, Token)).Mode);
            Assert.Equal(history, root.Chats.Public.Navigation.Messages.ToArray());
            Assert.Equal("Не терять черновик", root.Chats.Public.Navigation.Draft.Text);
            await ((IAsyncRelayCommand)region.Choices.Single(c => c.Header.EndsWith("Без региона")).Command).ExecuteAsync(null);
            await region.RefreshAsync(); await UntilAsync(() => region.Text == "Регион: без региона");
            Assert.Equal(0, f.Supervisor.ConnectCalls);
        }
        finally { await root.StopAsync(); }
    }
}
