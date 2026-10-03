using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    [Fact]
    public async Task StageCRealHundredThousandHistoryScrollIdentityReadDraftAndRecoverableRestart()
    {
        const int count = 100_000;
        var setup = Stopwatch.StartNew();
        await using var workspace = await Workspace.CreateAsync(count);
        var setupMilliseconds = setup.Elapsed.TotalMilliseconds;
        var root = workspace.Root;
        var publicChat = root.Chats.Public.Navigation;
        var privateChat = root.Chats.Private.Navigation;
        var history = publicChat.History;
        Assert.Equal(workspace.A.NodeId, root.ViewedNode?.Id);
        Assert.Equal(ConnectionSupervisorState.Offline, workspace.Supervisor.Snapshot.State);
        Assert.Equal(count, history.UnreadCount);
        Assert.Equal(count, history.Messages[^1].LocalSequence);

        var pageTimes = new List<double>();
        var peakDtos = 0;
        var peakManagedBytes = 0L;
        using var process = Process.GetCurrentProcess();
        void CheckWindow()
        {
            Assert.InRange(history.Messages.Count, 1, HistoryWindowViewModel.MaximumMessages);
            Assert.All(history.Messages, item => Assert.Equal(workspace.A.ConversationId, item.ConversationId));
            Assert.Equal(history.Messages.Count, history.Messages.Select(item => item.Id).Distinct().Count());
            Assert.Equal(history.Messages.Count, history.Messages[^1].LocalSequence - history.Messages[0].LocalSequence + 1);
            peakDtos = Math.Max(peakDtos, history.Messages.Count + privateChat.Messages.Count);
            peakManagedBytes = Math.Max(peakManagedBytes, GC.GetTotalMemory(false));
        }
        CheckWindow();
        // Visit every page, then reload every evicted range in the other direction.
        while (history.CanLoadOlder)
        {
            var first = history.Messages[0].LocalSequence;
            var started = Stopwatch.GetTimestamp();
            await history.LoadOlderAsync(Token);
            pageTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Assert.True(history.Messages[0].LocalSequence < first);
            CheckWindow();
        }
        Assert.Equal(1, history.Messages[0].LocalSequence);
        while (history.CanLoadNewer)
        {
            var last = history.Messages[^1].LocalSequence;
            var started = Stopwatch.GetTimestamp();
            await history.LoadNewerAsync(Token);
            pageTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Assert.True(history.Messages[^1].LocalSequence > last);
            CheckWindow();
        }
        Assert.Equal(count, history.Messages[^1].LocalSequence);
        Assert.InRange(peakDtos, 1, 2 * HistoryWindowViewModel.MaximumMessages);
        Assert.Equal(count, (await workspace.Storage.ReadStates.GetAsync(workspace.A.NodeId, workspace.A.ConversationId, Token)).UnreadCount);

        var sqlTimes = new List<double>();
        for (var index = 0; index < 20; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var page = await workspace.Storage.History.GetMessagesBeforeAsync(workspace.A.NodeId, workspace.A.ConversationId,
                null, HistoryWindowViewModel.PageSize, Token);
            sqlTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Assert.Equal(HistoryWindowViewModel.PageSize, page.Items.Count);
        }
        var searchStarted = Stopwatch.GetTimestamp();
        history.SearchText = "Node 1 needle";
        await UntilAsync(() => !history.IsSearching && history.SearchResults.Count == 1);
        var searchMilliseconds = Stopwatch.GetElapsedTime(searchStarted).TotalMilliseconds;
        await history.JumpToSearchResultAsync(history.SearchResults[0], Token);
        Assert.Equal(3, Assert.Single(history.Messages, item => item.IsSearchMatch).LocalSequence);
        Assert.Equal(count, history.UnreadCount); // Search and loading are not viewing.
        await history.JumpToFirstUnreadAsync(Token);
        root.Shell.SelectSection(ShellSection.Settings);
        history.ReportVisibleRange(1, 3, false, false);
        Assert.Equal(count, (await workspace.Storage.ReadStates.GetAsync(workspace.A.NodeId, workspace.A.ConversationId, Token)).UnreadCount);
        root.Shell.SelectSection(ShellSection.PublicChats);
        history.ReportVisibleRange(1, 3, true, false);
        await UntilAsync(() => history.UnreadCount == count - 3);
        await workspace.Reads.FlushAsync(Token);

        publicChat.Draft.Text = "C10 A public 🐈";
        privateChat.Draft.Text = "C10 A private 👋";
        workspace.Publish(workspace.A, 1);
        await UntilAsync(() => root.ActiveNode?.Id == workspace.A.NodeId);
        root.Shell.SelectSection(ShellSection.Connection);
        workspace.Publish(workspace.B, 2);
        await UntilAsync(() => root.ViewedNode?.Id == workspace.B.NodeId &&
            publicChat.SelectedConversation?.Entry.NodeId == workspace.B.NodeId &&
            privateChat.SelectedConversation?.Entry.NodeId == workspace.B.NodeId &&
            publicChat.Draft.CanEdit && privateChat.Draft.CanEdit);
        publicChat.Draft.Text = "C10 B public";
        privateChat.Draft.Text = "C10 B private";
        workspace.Publish(workspace.A, 3);
        await UntilAsync(() => root.ViewedNode?.Id == workspace.A.NodeId &&
            publicChat.Draft.Text == "C10 A public 🐈" && privateChat.Draft.Text == "C10 A private 👋");
        var lateB = await workspace.StoreAsync(workspace.B, "late B committed under A");
        workspace.Notifications.Publish(lateB);
        Assert.All(publicChat.Messages, item => Assert.Equal(workspace.A.ConversationId, item.ConversationId));
        Assert.All(privateChat.Messages, item => Assert.Equal(workspace.A.PrivateConversationId, item.ConversationId));
        Assert.Equal(0, workspace.Supervisor.ConnectCalls);

        // Node changes already flushed earlier drafts. Accept a new dirty revision
        // so shutdown really has a write to retry after the injected failure.
        publicChat.Draft.Text = "C10 A public final 🐈";
        privateChat.Draft.Text = "C10 A private final 👋";

        var ui = new CountingUi((IDesktopUiLifetime)root);
        await using var ingress = new MessageIngestor(workspace.Storage.IncomingMessages, TimeProvider.System);
        await using var connections = new DesktopConnectionLifecycle(workspace.Supervisor);
        var coordinator = new DesktopShutdownCoordinator(ui, connections, ingress,
            new SessionCompletionTracker(workspace.Storage.Sessions), workspace.Reads, workspace.Drafts,
            workspace.Preferences, NullLogger<DesktopShutdownCoordinator>.Instance);
        workspace.FailingDraftStore.FailWrites = true;
        await Assert.ThrowsAsync<DesktopShutdownException>(() => coordinator.ShutdownAsync(Token));
        Assert.False(coordinator.IsCompleted);
        Assert.True(root.HasError);
        workspace.FailingDraftStore.FailWrites = false;
        await Task.WhenAll(coordinator.ShutdownAsync(Token), coordinator.ShutdownAsync(Token));
        Assert.True(coordinator.IsCompleted);
        Assert.Equal(1, ui.StopCount);
        Assert.Equal("C10 B public", (await workspace.Storage.Drafts.GetAsync(workspace.B.Target, Token))!.Text);
        Assert.Equal("C10 B private", (await workspace.Storage.Drafts.GetAsync(workspace.B.PrivateTarget!, Token))!.Text);

        var restartStarted = Stopwatch.GetTimestamp();
        var restarted = workspace.CreateRoot();
        try
        {
            await restarted.LoadAsync(Token);
            Assert.Equal(workspace.A.NodeId, restarted.ViewedNode?.Id);
            Assert.Equal("C10 A public final 🐈", restarted.Chats.Public.Navigation.Draft.Text);
            Assert.Equal("C10 A private final 👋", restarted.Chats.Private.Navigation.Draft.Text);
            Assert.Equal(count - 3, restarted.Chats.Public.Navigation.History.UnreadCount);
            var restored = await workspace.Storage.History.GetMessagesBeforeAsync(workspace.B.NodeId, workspace.B.ConversationId, null, 100, Token);
            Assert.Contains(restored.Items, item => item.Id == lateB.MessageId);
        }
        finally { await restarted.StopAsync(); }
        pageTimes.Sort(); sqlTimes.Sort(); process.Refresh();
        var processPeak = process.PeakWorkingSet64 > 0
            ? $"{process.PeakWorkingSet64 / 1048576.0:F1}MiB" : "unavailable on this platform";
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"C10: setup={setupMilliseconds:F1}ms; scroll pages={pageTimes.Count}; DTO peak={peakDtos}; " +
            $"VM page p50/p95/max={pageTimes[pageTimes.Count / 2]:F2}/{pageTimes[(int)(pageTimes.Count * .95)]:F2}/{pageTimes[^1]:F2}ms; " +
            $"SQL page p50/p95={sqlTimes[10]:F2}/{sqlTimes[19]:F2}ms; search={searchMilliseconds:F2}ms; " +
            $"restart+stop={Stopwatch.GetElapsedTime(restartStarted).TotalMilliseconds:F2}ms; " +
            $"managed sampled peak={peakManagedBytes / 1048576.0:F1}MiB; process peak working set={processPeak}.");
    }
}
