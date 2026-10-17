using CommunityToolkit.Mvvm.Input;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
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
