using Microsoft.Extensions.Logging.Abstractions;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreMessenger.Desktop.Preferences;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

/// <summary>UI6: real SQLite/durable owners, fake supervisor/dispatcher/delays; no physical transport.</summary>
public sealed class UiWorkspaceIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AllScreensPreserveChatWindowSearchDraftAndHiddenUnread()
    {
        await using var workspace = await Workspace.CreateAsync();
        var root = workspace.Root;
        var history = root.Navigation.History;
        var draft = root.Navigation.Draft;
        history.SearchText = "needle";
        await UntilAsync(() => history.SearchResults.Count == 1);
        await history.JumpToSearchResultAsync(history.SearchResults[0], Token);
        draft.Text = "Черновик 👋\nне отправлять";
        var selected = root.SelectedConversation!.Id;
        var first = history.Messages[0];
        var results = history.SearchResults.ToArray();
        var before = await workspace.Storage.ReadStates.GetAsync(workspace.A.NodeId, selected!.Value, Token);
        foreach (var section in new[] { ShellSection.Devices, ShellSection.Connection, ShellSection.Settings })
        {
            history.ReportVisibleRange(null, null, true, false);
            root.Shell.SelectSection(section);
            Assert.False(history.IsWindowActive);
            Assert.False(root.IsChatWorkspaceVisible);
            if (section == ShellSection.Devices)
                await root.Devices.SelectAsync(root.Devices.Items[0]);
            root.Shell.SelectSection(ShellSection.PublicChats);
            Assert.Same(history, root.Navigation.History);
            Assert.Same(draft, root.Navigation.Draft);
            Assert.Same(first, history.Messages[0]);
            Assert.Equal(selected, root.SelectedConversation!.Id);
            Assert.Equal("needle", history.SearchText);
            Assert.Equal(results, history.SearchResults.ToArray());
            Assert.Equal("Черновик 👋\nне отправлять", draft.Text);
        }
        root.Shell.SelectSection(ShellSection.Devices);
        var inserted = await workspace.StoreAsync(workspace.A, "hidden incoming");
        workspace.Notifications.Publish(inserted);
        await UntilAsync(() => root.Navigation.Conversations.Any(item => item.Entry.ActivitySequence == inserted.LocalSequence));
        var after = await workspace.Storage.ReadStates.GetAsync(workspace.A.NodeId, selected.Value, Token);
        Assert.Equal(before.LastReadSequence, after.LastReadSequence);
        Assert.False(history.IsWindowActive);
        Assert.Equal(selected, root.SelectedConversation!.Id);
        Assert.Equal(0, workspace.Supervisor.ConnectCalls);
        Assert.Equal(0, workspace.Supervisor.DisconnectCalls);
    }

    [Fact]
    public async Task IdentityChangesOnConnectionScreenKeepHistoryDevicesAndDraftNodeScoped()
    {
        await using var workspace = await Workspace.CreateAsync();
        var root = workspace.Root;
        root.Navigation.Draft.Text = "A draft";
        root.Shell.SelectSection(ShellSection.Connection);
        workspace.Publish(workspace.B, 2);
        await UntilAsync(() => root.ViewedNode?.Id == workspace.B.NodeId && root.Devices.Items.Count == 1 &&
            root.Devices.Items[0].NodeId == workspace.B.NodeId && root.Navigation.Draft.CanEdit && root.Messages.Count > 0 &&
            root.SelectedConversation?.Entry.NodeId == workspace.B.NodeId);
        Assert.Equal(ShellSection.Connection, root.Shell.SelectedItem.Section);
        Assert.All(root.Messages, item => Assert.Equal(workspace.B.ConversationId, item.ConversationId));
        Assert.Equal(string.Empty, root.Navigation.Draft.Text);
        root.Navigation.Draft.Text = "B draft";
        workspace.Publish(workspace.A, 3);
        await UntilAsync(() => root.ViewedNode?.Id == workspace.A.NodeId && root.Navigation.Draft.Text == "A draft" &&
            root.Devices.Items.Count == 1 && root.Devices.Items[0].NodeId == workspace.A.NodeId);
        var staleCommit = await workspace.StoreAsync(workspace.B, "old B session committed");
        workspace.Notifications.Publish(staleCommit);
        await root.Devices.RefreshAsync(Token);
        Assert.All(root.Messages, item => Assert.Equal(workspace.A.ConversationId, item.ConversationId));
        Assert.All(root.Devices.Items, item => Assert.Equal(workspace.A.NodeId, item.NodeId));
        await workspace.Drafts.FlushAsync(workspace.A.Target, Token);
        await workspace.Drafts.FlushAsync(workspace.B.Target, Token);
        Assert.Equal("A draft", (await workspace.Storage.Drafts.GetAsync(workspace.A.Target, Token))!.Text);
        Assert.Equal("B draft", (await workspace.Storage.Drafts.GetAsync(workspace.B.Target, Token))!.Text);
        Assert.Equal(0, workspace.Supervisor.ConnectCalls);
    }

    [Theory]
    [InlineData(ShellSection.PublicChats)]
    [InlineData(ShellSection.PrivateChats)]
    [InlineData(ShellSection.Devices)]
    [InlineData(ShellSection.Connection)]
    [InlineData(ShellSection.Settings)]
    public async Task ShutdownFromEveryScreenRetriesDraftFailureAndRestoresDurableState(ShellSection section)
    {
        await using var workspace = await Workspace.CreateAsync();
        var root = workspace.Root;
        var selected = root.SelectedConversation!.Entry;
        root.Navigation.Draft.Text = "Последний символ 🐈\n";
        root.SelectedTheme = root.ThemeOptions.Single(option => option.Value == DesktopThemePreference.Dark);
        root.UpdateWindowPlacement(new WindowPlacement(80, 60, 1040, 700, false));
        root.Shell.SelectSection(section);
        // Private changes tab asynchronously. Quiesce must preserve the already accepted old owner too.
        workspace.FailingDraftStore.FailWrites = true;
        var ui = new CountingUi((IDesktopUiLifetime)root);
        await using var ingress = new MessageIngestor(workspace.Storage.IncomingMessages, TimeProvider.System);
        await using var connections = new DesktopConnectionLifecycle(workspace.Supervisor);
        var coordinator = new DesktopShutdownCoordinator(ui, connections, ingress,
            new SessionCompletionTracker(workspace.Storage.Sessions), workspace.Reads, workspace.Drafts,
            workspace.Preferences, NullLogger<DesktopShutdownCoordinator>.Instance);
        await Assert.ThrowsAsync<DesktopShutdownException>(() => coordinator.ShutdownAsync(Token));
        Assert.False(coordinator.IsCompleted);
        Assert.True(root.HasError);
        Assert.True(workspace.Drafts.IsPaused);
        Assert.Equal(1, ui.StopCount);
        var error = root.ErrorMessage;
        foreach (var screen in Enum.GetValues<ShellSection>())
        {
            root.Shell.SelectSection(screen);
            Assert.Equal(error, root.ErrorMessage);
        }
        workspace.FailingDraftStore.FailWrites = false;
        await Task.WhenAll(coordinator.ShutdownAsync(Token), coordinator.ShutdownAsync(Token));
        Assert.True(coordinator.IsCompleted);
        Assert.Equal(1, ui.StopCount);
        Assert.False(workspace.Drafts.IsPaused);
        Assert.Equal("Последний символ 🐈\n", (await workspace.Storage.Drafts.GetAsync(workspace.A.Target, Token))!.Text);
        var restarted = workspace.CreateRoot();
        await restarted.LoadAsync(Token);
        Assert.Equal(workspace.A.NodeId, restarted.ViewedNode?.Id);
        restarted.Shell.SelectSection(ShellSection.PublicChats);
        await UntilAsync(() => restarted.SelectedConversation?.StableKey == selected.StableKey && restarted.Navigation.Draft.CanEdit);
        Assert.Equal(selected.StableKey, restarted.SelectedConversation?.StableKey);
        Assert.Equal("Последний символ 🐈\n", restarted.Navigation.Draft.Text);
        Assert.Equal(DesktopThemePreference.Dark, restarted.SelectedTheme.Value);
        Assert.Equal(1040, restarted.SavedWindowPlacement?.Width);
        await restarted.StopAsync();
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class CountingUi(IDesktopUiLifetime inner) : IDesktopUiLifetime
    {
        public int StopCount { get; private set; }
        public Task StopAsync() { StopCount++; return inner.StopAsync(); }
        public Task ReportShutdownFailureAsync(Exception error) => inner.ReportShutdownFailureAsync(error);
    }
    private sealed class FaultingDraftStore(IDraftStore inner) : IDraftStore
    {
        public bool FailWrites { get; set; }
        public Task<DraftRecord?> GetAsync(DraftTarget target, CancellationToken token = default) => inner.GetAsync(target, token);
        public Task<DraftRecord?> SaveAsync(DraftTarget target, string text, DateTimeOffset updated, CancellationToken token = default) =>
            FailWrites ? Task.FromException<DraftRecord?>(new IOException("UI6 injected disk failure")) : inner.SaveAsync(target, text, updated, token);
    }
    private sealed record NodeData(Guid NodeId, Guid SessionId, Guid ConversationId, ChannelBindingRecord Binding, DraftTarget Target);

    private sealed class Workspace : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MeshCoreMessenger.UI6.Tests", Guid.NewGuid().ToString("N"));
        public LocalStorage Storage { get; private set; } = null!;
        public NodeData A { get; private set; } = null!;
        public NodeData B { get; private set; } = null!;
        public MainWindowViewModel Root { get; private set; } = null!;
        public FakeConnectionSupervisor Supervisor { get; } = new();
        public FakeMessageCommitNotifications Notifications { get; } = new();
        public Guid ProfileId { get; } = Guid.NewGuid();
        public FaultingDraftStore FailingDraftStore { get; private set; } = null!;
        public DraftWriteTracker Drafts { get; private set; } = null!;
        public ConversationReadStateTracker Reads { get; private set; } = null!;
        public DesktopPreferences Preferences { get; private set; } = null!;

        public static async Task<Workspace> CreateAsync()
        {
            var w = new Workspace();
            w.Storage = await LocalStorage.OpenAsync(DesktopAppPaths.CreateForDirectory(w._directory), Token);
            await w.Storage.ConnectionProfiles.SaveAsync(new ConnectionProfile
            {
                Id = w.ProfileId, Name = "UI6 fake endpoint", Transport = ConnectionTransportKind.Tcp,
                TcpHost = "127.0.0.1", TcpPort = 1, AutoConnect = false, Reconnect = false, CreatedUtc = Now, UpdatedUtc = Now,
            }, Token);
            w.A = await w.SeedNodeAsync(1);
            w.B = await w.SeedNodeAsync(2);
            await w.Storage.Settings.SetAsync(MainWindowViewModel.LastConnectedNodeSettingKey, w.A.NodeId.ToString("D"), Token);
            w.Root = w.CreateRoot();
            await w.Root.LoadAsync(Token);
            return w;
        }
        public MainWindowViewModel CreateRoot()
        {
            FailingDraftStore = new FaultingDraftStore(Storage.Drafts);
            Drafts = new DraftWriteTracker(FailingDraftStore, TimeProvider.System);
            Reads = new ConversationReadStateTracker(Storage.ReadStates);
            Preferences = new DesktopPreferences(Storage.Settings);
            var profiles = new ConnectionProfilesViewModel(new ConnectionProfileManager(Storage.ConnectionProfiles, Storage.Settings, TimeProvider.System),
                Supervisor, new EmptyPorts(), NullLogger<ConnectionProfilesViewModel>.Instance);
            return new MainWindowViewModel(Storage.ConversationDirectory, Storage.History, Storage.ReadStates, Reads,
                Drafts, Storage.Nodes, Storage.Settings, Preferences, profiles, Supervisor, Notifications,
                new ImmediateUiDispatcher(), new ImmediateSearchDelay(), new ControlledDraftDelay(), NullLogger<MainWindowViewModel>.Instance);
        }
        public void Publish(NodeData node, long generation) => Supervisor.Publish(new ConnectionSupervisorSnapshot(
            ConnectionSupervisorState.Online, generation, ProfileId, node.SessionId, node.NodeId, null, null));
        private async Task<NodeData> SeedNodeAsync(byte key)
        {
            var node = await Storage.Nodes.FindOrCreateAsync(Enumerable.Repeat(key, 32).ToArray(), $"Node {key}", Now, Token);
            var session = Guid.NewGuid();
            await Storage.Sessions.CreateAsync(new SessionRecord(session, ProfileId, node.Id, Now, null, null), Token);
            await Storage.Sessions.EndAsync(session, Now, "UI6Fake", Token);
            var fingerprint = Enumerable.Repeat((byte)42, 32).ToArray();
            var snapshot = await Storage.Directories.ApplySnapshotAsync(node.Id, session,
                [new DirectoryContactSnapshot(Enumerable.Repeat((byte)7, 32).ToArray(), "Same repeater", 2, 0, new byte[64], Now, 0, 0)],
                [new DirectoryChannelSnapshot(0, "Shared name", fingerprint, ChannelAccessKind.Unknown)], Now, Token);
            var binding = Assert.Single(snapshot.ActiveBindings);
            var partial = new NodeData(node.Id, session, Guid.Empty, binding, null!);
            StoredIncomingMessage last = null!;
            for (var index = 0; index < 12; index++)
                last = await StoreAsync(partial, index == 2 ? $"Node {key} needle" : $"Node {key} message {index}");
            await Storage.Settings.SetAsync(ConversationNavigationViewModel.TabSettingKey(node.Id), MessengerNavigationTab.Channels.ToString(), Token);
            return partial with { ConversationId = last.ConversationId, Target = new DraftTarget(node.Id, last.ConversationId, ConversationKind.Channel, fingerprint) };
        }
        public Task<StoredIncomingMessage> StoreAsync(NodeData node, string text) => Storage.IncomingMessages.StoreAsync(
            new IncomingMessageEnvelope(Guid.NewGuid(), node.SessionId, node.NodeId,
                new ChannelMessage(0, 1, MessageTextType.Plain, Now, text, 0), Now, node.Binding, null), Token);
        public async ValueTask DisposeAsync()
        {
            if (Root is not null) await Root.StopAsync();
            await Storage.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
    private sealed class EmptyPorts : ISerialPortCatalog
    {
        public Task<IReadOnlyList<string>> GetPortNamesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
