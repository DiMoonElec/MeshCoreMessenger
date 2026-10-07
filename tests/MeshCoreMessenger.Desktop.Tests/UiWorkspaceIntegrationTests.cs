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
public sealed partial class UiWorkspaceIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PublicPrivateRoundTripsRetainIndependentSelectionWindowSearchDraftAndNarrowNavigation()
    {
        await using var workspace = await Workspace.CreateAsync();
        var root = workspace.Root;
        var channels = root.Chats.Public.Navigation;
        var personal = root.Chats.Private.Navigation;
        Assert.NotSame(channels, personal);
        Assert.NotSame(channels.History, personal.History);
        Assert.NotSame(channels.Draft, personal.Draft);
        channels.History.SearchText = "needle";
        await UntilAsync(() => channels.History.SearchResults.Count == 1);
        await channels.History.JumpToSearchResultAsync(channels.History.SearchResults[0], Token);
        channels.Draft.Text = "public draft 🐈";
        channels.SetNarrowLayout(true);
        await channels.SelectConversationAsync(channels.SelectedConversation, Token);
        var publicSelection = channels.SelectedConversation!.StableKey;
        var publicWindow = channels.Messages.ToArray();
        channels.History.ReportVisibleRange(publicWindow[1].LocalSequence, publicWindow[2].LocalSequence, true, false);
        root.Shell.SelectSection(ShellSection.PrivateChats);
        Assert.Same(personal, root.Navigation);
        Assert.False(channels.History.IsWindowActive);
        personal.History.SearchText = "private needle";
        await UntilAsync(() => personal.History.SearchResults.Count == 1);
        await personal.History.JumpToSearchResultAsync(personal.History.SearchResults[0], Token);
        personal.Draft.Text = "private draft 👋";
        personal.SetNarrowLayout(true);
        personal.BackCommand.Execute(null);
        var privateSelection = personal.SelectedConversation!.StableKey;
        var privateWindow = personal.Messages.ToArray();
        channels.DirectorySearchText = "Shared";
        personal.DirectorySearchText = "Personal";
        await UntilAsync(() => !channels.IsDirectorySearching && !personal.IsDirectorySearching);
        for (var index = 0; index < 20; index++)
        {
            root.Shell.SelectSection(ShellSection.PublicChats);
            Assert.Same(channels, root.Navigation);
            Assert.Equal(publicSelection, root.SelectedConversation?.StableKey);
            Assert.Equal(publicWindow, channels.Messages.ToArray());
            Assert.Equal("needle", channels.History.SearchText);
            Assert.Equal("Shared", channels.DirectorySearchText);
            Assert.Equal("public draft 🐈", channels.Draft.Text);
            Assert.True(channels.IsDetailVisible);
            root.Shell.SelectSection(ShellSection.PrivateChats);
            Assert.Same(personal, root.Navigation);
            Assert.Equal(privateSelection, root.SelectedConversation?.StableKey);
            Assert.Equal(privateWindow, personal.Messages.ToArray());
            Assert.Equal("private needle", personal.History.SearchText);
            Assert.Equal("Personal", personal.DirectorySearchText);
            Assert.Equal("private draft 👋", personal.Draft.Text);
            Assert.False(personal.IsDetailVisible);
        }
        Assert.Equal(0, workspace.Supervisor.ConnectCalls);
    }

    [Fact]
    public async Task IncomingMessageInHiddenOtherWorkspaceDoesNotReadOrReplaceItsWindow()
    {
        await using var workspace = await Workspace.CreateAsync();
        var root = workspace.Root;
        var hidden = root.Chats.Private.Navigation;
        hidden.History.SearchText = "private needle";
        await UntilAsync(() => hidden.History.SearchResults.Count == 1);
        var original = hidden.Messages.ToArray();
        var before = await workspace.Storage.ReadStates.GetAsync(workspace.A.NodeId, workspace.A.PrivateConversationId, Token);
        var inserted = await workspace.StorePrivateAsync(workspace.A, "hidden private incoming");
        workspace.Notifications.Publish(inserted);
        await UntilAsync(() => hidden.History.PendingNewMessageCount == 1);
        Assert.Equal(original, hidden.Messages.ToArray());
        Assert.Equal("private needle", hidden.History.SearchText);
        Assert.Equal(before.LastReadSequence, (await workspace.Storage.ReadStates.GetAsync(workspace.A.NodeId, workspace.A.PrivateConversationId, Token)).LastReadSequence);
        Assert.False(hidden.History.IsWindowActive);
        Assert.Same(root.Chats.Public.Navigation, root.Navigation);
        root.Shell.SelectSection(ShellSection.PrivateChats);
        Assert.Equal(original, hidden.Messages.ToArray());
        await hidden.History.JumpToLatestAsync(Token);
        Assert.Contains(hidden.Messages, message => message.Body == "hidden private incoming");
    }

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
        root.Chats.Private.Navigation.Draft.Text = "A private draft";
        root.Shell.SelectSection(ShellSection.Connection);
        workspace.Publish(workspace.B, 2);
        await UntilAsync(() => root.ViewedNode?.Id == workspace.B.NodeId && root.Devices.Items.Count == 1 &&
            root.Devices.Items[0].NodeId == workspace.B.NodeId && root.Navigation.Draft.CanEdit && root.Messages.Count > 0 &&
            root.SelectedConversation?.Entry.NodeId == workspace.B.NodeId && root.Chats.Private.Navigation.Draft.CanEdit);
        Assert.Equal(ShellSection.Connection, root.Shell.SelectedItem.Section);
        Assert.All(root.Messages, item => Assert.Equal(workspace.B.ConversationId, item.ConversationId));
        Assert.Equal(string.Empty, root.Navigation.Draft.Text);
        root.Navigation.Draft.Text = "B draft";
        root.Chats.Private.Navigation.Draft.Text = "B private draft";
        workspace.Publish(workspace.A, 3);
        await UntilAsync(() => root.ViewedNode?.Id == workspace.A.NodeId && root.Navigation.Draft.Text == "A draft" &&
            root.Devices.Items.Count == 1 && root.Devices.Items[0].NodeId == workspace.A.NodeId &&
            root.Chats.Private.Navigation.Draft.Text == "A private draft");
        var staleCommit = await workspace.StoreAsync(workspace.B, "old B session committed");
        workspace.Notifications.Publish(staleCommit);
        await root.Devices.RefreshAsync(Token);
        Assert.All(root.Messages, item => Assert.Equal(workspace.A.ConversationId, item.ConversationId));
        Assert.All(root.Chats.Private.Navigation.Messages, item => Assert.Equal(workspace.A.PrivateConversationId, item.ConversationId));
        Assert.All(root.Devices.Items, item => Assert.Equal(workspace.A.NodeId, item.NodeId));
        await workspace.Drafts.FlushAsync(workspace.A.Target, Token);
        await workspace.Drafts.FlushAsync(workspace.B.Target, Token);
        await workspace.Drafts.FlushAsync(workspace.A.PrivateTarget!, Token);
        await workspace.Drafts.FlushAsync(workspace.B.PrivateTarget!, Token);
        Assert.Equal("A draft", (await workspace.Storage.Drafts.GetAsync(workspace.A.Target, Token))!.Text);
        Assert.Equal("B draft", (await workspace.Storage.Drafts.GetAsync(workspace.B.Target, Token))!.Text);
        Assert.Equal("A private draft", (await workspace.Storage.Drafts.GetAsync(workspace.A.PrivateTarget!, Token))!.Text);
        Assert.Equal("B private draft", (await workspace.Storage.Drafts.GetAsync(workspace.B.PrivateTarget!, Token))!.Text);
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
        root.Chats.Private.Navigation.Draft.Text = "Личный черновик 👋";
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
        Assert.False(root.Chats.Public.IsVisible);
        Assert.False(root.Chats.Private.IsVisible);
        Assert.False(root.Chats.Public.Navigation.History.IsWindowActive);
        Assert.False(root.Chats.Private.Navigation.History.IsWindowActive);
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
        Assert.Equal("Личный черновик 👋", (await workspace.Storage.Drafts.GetAsync(workspace.A.PrivateTarget!, Token))!.Text);
        var restarted = workspace.CreateRoot();
        await restarted.LoadAsync(Token);
        Assert.Equal(workspace.A.NodeId, restarted.ViewedNode?.Id);
        restarted.Shell.SelectSection(ShellSection.PublicChats);
        await UntilAsync(() => restarted.SelectedConversation?.StableKey == selected.StableKey && restarted.Navigation.Draft.CanEdit);
        Assert.Equal(selected.StableKey, restarted.SelectedConversation?.StableKey);
        Assert.Equal("Последний символ 🐈\n", restarted.Navigation.Draft.Text);
        Assert.Equal("Личный черновик 👋", restarted.Chats.Private.Navigation.Draft.Text);
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
    private sealed record NodeData(Guid NodeId, Guid SessionId, Guid ConversationId, ChannelBindingRecord Binding, DraftTarget Target,
        Guid PrivateConversationId = default, DraftTarget? PrivateTarget = null);

    private sealed class Workspace : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MeshCoreMessenger.UI6.Tests", Guid.NewGuid().ToString("N"));
        public LocalStorage Storage { get; private set; } = null!;
        public string StoragePath() => Path.Combine(_directory, "messenger.db");
        public NodeData A { get; private set; } = null!;
        public NodeData B { get; private set; } = null!;
        public MainWindowViewModel Root { get; set; } = null!;
        public FakeConnectionSupervisor Supervisor { get; } = new();
        public FakeMessageCommitNotifications Notifications { get; } = new();
        public Guid ProfileId { get; } = Guid.NewGuid();
        public FaultingDraftStore FailingDraftStore { get; private set; } = null!;
        public DraftWriteTracker Drafts { get; private set; } = null!;
        public ConversationReadStateTracker Reads { get; private set; } = null!;
        public DesktopPreferences Preferences { get; private set; } = null!;

        public static async Task<Workspace> CreateAsync(int publicMessageCount = 12, bool variableMessageHeight = false)
        {
            var w = new Workspace();
            w.Storage = await LocalStorage.OpenAsync(DesktopAppPaths.CreateForDirectory(w._directory), Token);
            await w.Storage.ConnectionProfiles.SaveAsync(new ConnectionProfile
            {
                Id = w.ProfileId,
                Name = "UI6 fake endpoint",
                Transport = ConnectionTransportKind.Tcp,
                TcpHost = "127.0.0.1",
                TcpPort = 1,
                AutoConnect = false,
                Reconnect = false,
                CreatedUtc = Now,
                UpdatedUtc = Now,
            }, Token);
            w.A = await w.SeedNodeAsync(1, publicMessageCount, variableMessageHeight);
            w.B = await w.SeedNodeAsync(2);
            await w.Storage.Settings.SetAsync(MainWindowViewModel.LastConnectedNodeSettingKey, w.A.NodeId.ToString("D"), Token);
            w.Root = w.CreateRoot();
            await w.Root.LoadAsync(Token);
            return w;
        }
        public MainWindowViewModel CreateRoot(IConversationReadStateStore? readStore = null, IUiDispatcher? dispatcher = null,
            ISendReadinessReader? sendReadiness = null, IMessageService? messageService = null,
            Microsoft.Extensions.Logging.ILogger<MainWindowViewModel>? logger = null, IContactRouteService? contactRoutes = null,
            ISettingsStore? preferencesStore = null)
        {
            FailingDraftStore = new FaultingDraftStore(Storage.Drafts);
            Drafts = new DraftWriteTracker(FailingDraftStore, TimeProvider.System);
            readStore ??= Storage.ReadStates;
            Reads = new ConversationReadStateTracker(readStore);
            Preferences = new DesktopPreferences(preferencesStore ?? Storage.Settings);
            var profiles = new ConnectionProfilesViewModel(new ConnectionProfileManager(Storage.ConnectionProfiles, Storage.Settings, TimeProvider.System),
                Supervisor, new EmptyPorts(), NullLogger<ConnectionProfilesViewModel>.Instance);
            return new MainWindowViewModel(Storage.ConversationDirectory, Storage.History, readStore, Reads,
                Drafts, Storage.Nodes, Storage.Settings, Preferences, profiles, Supervisor, Notifications,
                dispatcher ?? new ImmediateUiDispatcher(), new ImmediateSearchDelay(), new ControlledDraftDelay(), logger ?? NullLogger<MainWindowViewModel>.Instance,
                sendReadiness: sendReadiness ?? new SendReadinessReader(Storage.Directories, Storage.ConversationDirectory),
                messageService: messageService, outgoingMessages: Storage.OutgoingMessages,
                historyClear: new HistoryClearService(Storage.HistoryClear, new(), Reads, new OutgoingAttemptWriteTracker(Storage.OutgoingMessages)), contactRoutes: contactRoutes, directoryUpdates: Storage.Directories,
                contactDeliveries: Storage.ContactDeliveries);
        }
        public void Publish(NodeData node, long generation) => Supervisor.Publish(new ConnectionSupervisorSnapshot(
            ConnectionSupervisorState.Online, generation, ProfileId, node.SessionId, node.NodeId, null, null));
        private async Task<NodeData> SeedNodeAsync(byte key, int publicMessageCount = 12, bool variableMessageHeight = false)
        {
            var node = await Storage.Nodes.FindOrCreateAsync(Enumerable.Repeat(key, 32).ToArray(), $"Node {key}", Now, Token);
            var session = Guid.NewGuid();
            await Storage.Sessions.CreateAsync(new SessionRecord(session, ProfileId, node.Id, Now, null, null), Token);
            await Storage.Sessions.EndAsync(session, Now, "UI6Fake", Token);
            var fingerprint = Enumerable.Repeat((byte)42, 32).ToArray();
            var snapshot = await Storage.Directories.ApplySnapshotAsync(node.Id, session,
                [new DirectoryContactSnapshot(Enumerable.Repeat((byte)7, 32).ToArray(), "Same repeater", 2, 0, new byte[64], Now, 0, 0),
                 new DirectoryContactSnapshot(Enumerable.Repeat((byte)9, 32).ToArray(), "Personal chat", 1, 0, new byte[64], Now, 0, 0)],
                [new DirectoryChannelSnapshot(0, "Shared name", fingerprint, ChannelAccessKind.Unknown)], Now, Token);
            var binding = Assert.Single(snapshot.ActiveBindings);
            var partial = new NodeData(node.Id, session, Guid.Empty, binding, null!);
            StoredIncomingMessage last = null!;
            for (var index = 0; index < publicMessageCount; index++)
                last = await StoreAsync(partial, index == 2 ? $"Node {key} needle" :
                    $"Node {key} message {index}" + (variableMessageHeight ? string.Concat(Enumerable.Repeat("\nСтрока с emoji 🐈 и @[Mention]", index % 5)) : string.Empty));
            await Storage.Settings.SetAsync(ConversationNavigationViewModel.TabSettingKey(node.Id), MessengerNavigationTab.Channels.ToString(), Token);
            StoredIncomingMessage privateLast = null!;
            for (var index = 0; index < 12; index++)
                privateLast = await StorePrivateAsync(partial, index == 2 ? "private needle" : $"private message {index}");
            return partial with
            {
                ConversationId = last.ConversationId,
                Target = new DraftTarget(node.Id, last.ConversationId, ConversationKind.Channel, fingerprint),
                PrivateConversationId = privateLast.ConversationId,
                PrivateTarget = new DraftTarget(node.Id, privateLast.ConversationId, ConversationKind.Contact, Enumerable.Repeat((byte)9, 32).ToArray())
            };
        }
        public Task<StoredIncomingMessage> StoreAsync(NodeData node, string text) => Storage.IncomingMessages.StoreAsync(
            new IncomingMessageEnvelope(Guid.NewGuid(), node.SessionId, node.NodeId,
                new ChannelMessage(0, 1, MessageTextType.Plain, Now, text, 0), Now, node.Binding, null), Token);
        public Task<StoredIncomingMessage> StorePrivateAsync(NodeData node, string text) => Storage.IncomingMessages.StoreAsync(
            new IncomingMessageEnvelope(Guid.NewGuid(), node.SessionId, node.NodeId,
                new ContactMessage(Enumerable.Repeat((byte)9, 6).ToArray(), 1, MessageTextType.Plain, Now, text, ReadOnlyMemory<byte>.Empty, 0), Now, null, null), Token);
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
