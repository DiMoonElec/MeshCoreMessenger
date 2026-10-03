using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Controls;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Views.Chat;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    // Called by the optional native audit executable, not by the headless xUnit suite.
    public static class NativeAudit
    {
        public static Task RunAsync(bool enforce) => AuditViewportAsync(enforce);
    }

    private static async Task AuditViewportAsync(bool enforce)
    {
        await using var workspace = await Workspace.CreateAsync(1_000, variableMessageHeight: true);
        var firstPage = await workspace.Storage.History.GetMessagesAfterAsync(workspace.A.NodeId, workspace.A.ConversationId, null, 500);
        var target = firstPage.LastPosition!;
        var around = await workspace.Storage.History.GetMessagesAroundAsync(target, 20, 79);
        var unread = await workspace.Storage.History.GetMessagesAfterAsync(workspace.A.NodeId, workspace.A.ConversationId, around.LastPosition, 1);
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        foreach (var scenario in new[] { "forward-trim-500", "forward-no-trim", "backward" })
        {
            var readStore = new AuditReads(workspace.Storage.ReadStates, workspace.A.NodeId, workspace.A.ConversationId,
                around.LastPosition!.LocalSequence, unread.FirstPosition!);
            var root = workspace.CreateRoot(readStore, new NativeDispatcher());
            await root.LoadAsync();
            root.Shell.SelectSection(ShellSection.PublicChats);
            var view = new ConversationView { DataContext = root.Chats.Public };
            var window = new Window { Content = view, Width = 640, Height = 540, RequestedThemeVariant = theme };
            window.Show(); window.Activate();
            await SettleAsync(window);
            var history = root.Chats.Public.Navigation.History;
            var list = view.GetVisualDescendants().OfType<VirtualizedHistoryListBox>().Single();
            var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            // Fixture preparation only: suppress viewport reports and detach the adapter
            // while choosing a known range. Production scrolling is untouched during audit.
            var pending = typeof(ConversationView).GetField("_pendingScroll", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var handler = (EventHandler<HistoryScrollRequestEventArgs>)Delegate.CreateDelegate(
                typeof(EventHandler<HistoryScrollRequestEventArgs>), view,
                typeof(ConversationView).GetMethod("OnScrollRequested", BindingFlags.NonPublic | BindingFlags.Instance)!);
            history.ScrollRequested -= handler;
            pending.SetValue(view, true);
            await history.JumpToSearchResultAsync(new HistorySearchResultListItem(new HistoryMessageSearchResult(
                target, MessageDirection.Incoming, "target", DateTimeOffset.UnixEpoch)));
            if (scenario == "forward-trim-500")
                for (var index = 0; index < 4; index++) await history.LoadOlderAsync();
            list.ScrollIntoView(history.Messages[^1]);
            await SettleAsync(window);
            viewer.Offset = new Vector(0, scenario == "backward" ? 320 :
                Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height - 160));
            await SettleAsync(window);
            history.ScrollRequested += handler;
            pending.SetValue(view, false);
            view.GetType().GetMethod("ReportViewport", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, null);
            var first = list.GetRealizedContainers()
                .Select(control => (Control: control, Item: control.DataContext as HistoryMessageListItem,
                    Y: control.TranslatePoint(default, list)!.Value.Y))
                .Where(item => item.Item is not null && item.Y + item.Control.Bounds.Height > 0 && item.Y < list.Bounds.Height)
                .OrderBy(item => item.Item!.LocalSequence).First();
            var sequence = first.Item!.LocalSequence;
            var before = first.Y;
            var oldCount = history.Messages.Count;
            readStore.Advances.Clear();
            if (scenario == "backward") await history.LoadOlderAsync();
            else await history.LoadNewerAsync();
            await SettleAsync(window);
            var container = list.ContainerFromIndex(list.Items.IndexOf(history.Messages.Single(item => item.LocalSequence == sequence)));
            var after = container?.TranslatePoint(default, list)?.Y;
            var delta = after is { } y ? y - before : double.PositiveInfinity;
            Console.WriteLine($"{theme}/{scenario}: count={oldCount}->{history.Messages.Count}; anchor={sequence}; " +
                $"Y before={before:F3}, after={after:F3}, delta={delta:F3}px; unread advances={readStore.Advances.Count}; active={window.IsActive}");
            if (enforce && (Math.Abs(delta) > 1 || readStore.Advances.Count != 0 || !window.IsActive || (bool)pending.GetValue(view)!))
                throw new InvalidOperationException($"Viewport audit failed: {scenario}");
            await history.LoadNewerAsync();
            root.Shell.SelectSection(ShellSection.Settings);
            await SettleAsync(window);
            if (enforce && ((bool)pending.GetValue(view)! || readStore.Advances.Count != 0))
                throw new InvalidOperationException("Hidden viewport did not cancel restoration/read reporting.");
            window.Close();
            await root.StopAsync();
        }
    }

    private static async Task SettleAsync(Window window)
    {
        // Drain queued layout/viewport callbacks; no wall-clock sleeps.
        for (var index = 0; index < 6; index++)
        {
            window.UpdateLayout();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    private sealed class NativeDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) =>
            Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).GetTask();
    }

    private sealed class AuditReads(IConversationReadStateStore inner, Guid node, Guid chat, long watermark,
        HistoryMessagePosition firstUnread) : IConversationReadStateStore
    {
        public List<HistoryMessagePosition> Advances { get; } = [];
        public Task<ConversationReadState> GetAsync(Guid nodeId, Guid conversationId, CancellationToken token = default) =>
            nodeId == node && conversationId == chat
                ? Task.FromResult(new ConversationReadState(node, chat, watermark, 1_000 - watermark, firstUnread))
                : inner.GetAsync(nodeId, conversationId, token);
        public Task<ConversationReadState> AdvanceAsync(HistoryMessagePosition through, CancellationToken token = default)
        {
            Advances.Add(through);
            return GetAsync(through.NodeId, through.ConversationId, token);
        }
    }
}
