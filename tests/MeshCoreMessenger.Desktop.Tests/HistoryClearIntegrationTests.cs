using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    [Fact]
    public async Task HistoryClearUpdatesEmptySearchPreviewUnreadAndPreservesDraftOtherWorkspaceNodeAndRestart()
    {
        await using var w = await Workspace.CreateAsync();
        var chat = w.Root.Chats.Public;
        var selection = chat.SelectedConversation!.StableKey;
        chat.Navigation.History.SearchText = "needle";
        await UntilAsync(() => chat.Navigation.History.HasSearchResults);
        chat.Navigation.Draft.Text = "keep draft";
        await w.Drafts.FlushAsync(w.A.Target, Token);
        var otherHistory = await w.Storage.History.GetMessagesAsync(w.B.NodeId, w.B.ConversationId, null, 50, Token);
        await chat.HistoryClear.RefreshAsync();
        var target = Assert.IsType<MeshCoreMessenger.Desktop.ViewModels.HistoryClearTarget>(chat.HistoryClear.Capture());
        await chat.HistoryClear.ClearAsync(target);
        Assert.Null(chat.HistoryClear.ErrorMessage);
        Assert.Empty(chat.Navigation.Messages);
        Assert.Empty(chat.Navigation.History.SearchResults);
        Assert.Equal("", chat.Navigation.History.SearchText);
        Assert.Equal(selection, chat.SelectedConversation!.StableKey);
        Assert.Equal("Нет сообщений", chat.SelectedConversation.Preview);
        Assert.Equal(0, chat.SelectedConversation.UnreadCount);
        Assert.Equal("keep draft", chat.Navigation.Draft.Text);
        Assert.Equal(otherHistory, await w.Storage.History.GetMessagesAsync(w.B.NodeId, w.B.ConversationId, null, 50, Token));
        Assert.Equal(12, (await w.Storage.History.GetMessagesAsync(w.A.NodeId, w.A.PrivateConversationId, null, 50, Token)).Count);
        Assert.False(chat.HistoryClear.CanClear);
        Assert.Equal(0, w.Supervisor.ConnectCalls);
        var fresh = await w.StoreAsync(w.A, "after clear");
        await chat.Navigation.HandleCommittedMessageAsync(fresh, Token);
        Assert.Equal("after clear", Assert.Single(chat.Navigation.Messages).Body);
        await w.Root.StopAsync();
        w.Root = w.CreateRoot(); await w.Root.LoadAsync(Token);
        Assert.Equal("after clear", Assert.Single(w.Root.Chats.Public.Navigation.Messages).Body);
        Assert.Equal("keep draft", w.Root.Chats.Public.Navigation.Draft.Text);
    }

    [Fact]
    public async Task PreparingConfirmationDoesNotDeleteAndSwitchingWorkspaceKeepsCapturedTarget()
    {
        await using var w = await Workspace.CreateAsync();
        var chat = w.Root.Chats.Public;
        await chat.HistoryClear.RefreshAsync();
        var target = chat.HistoryClear.Capture()!;
        Assert.Equal(12, chat.Navigation.Messages.Count); // Opening/cancelling confirmation calls no clear.
        w.Root.Shell.SelectSection(ShellSection.PrivateChats);
        var privateMessages = w.Root.Chats.Private.Navigation.Messages.Select(m => m.Id).ToArray();
        await chat.HistoryClear.ClearAsync(target);
        Assert.Empty(await w.Storage.History.GetMessagesAsync(w.A.NodeId, target.ConversationId, null, 50, Token));
        Assert.Equal(privateMessages, w.Root.Chats.Private.Navigation.Messages.Select(m => m.Id));
        Assert.Equal(ShellSection.PrivateChats, w.Root.Shell.SelectedItem.Section);
    }

    [Fact]
    public async Task FailedHistoryClearPreservesVisibleSearchDraftAndOffersAnotherAttempt()
    {
        await using var w = await Workspace.CreateAsync();
        var chat = w.Root.Chats.Public;
        chat.Navigation.Draft.Text = "keep";
        chat.Navigation.History.SearchText = "needle";
        await UntilAsync(() => chat.Navigation.History.HasSearchResults);
        var before = chat.Navigation.Messages.Select(m => m.Id).ToArray();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={w.StoragePath()}");
        await connection.OpenAsync(Token);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER RejectClear BEFORE DELETE ON Messages BEGIN SELECT RAISE(ABORT,'blocked'); END;";
        command.ExecuteNonQuery();
        await chat.HistoryClear.RefreshAsync();
        await chat.HistoryClear.ClearAsync(chat.HistoryClear.Capture()!);
        Assert.NotNull(chat.HistoryClear.ErrorMessage);
        Assert.Equal(before, chat.Navigation.Messages.Select(m => m.Id));
        Assert.True(chat.Navigation.History.HasSearchResults);
        Assert.Equal("keep", chat.Navigation.Draft.Text);
        Assert.True(chat.HistoryClear.CanClear);
    }
}
