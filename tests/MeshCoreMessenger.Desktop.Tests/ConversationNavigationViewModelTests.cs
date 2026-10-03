using Microsoft.Extensions.Logging.Abstractions;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreMessenger.Desktop.Views;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class ConversationNavigationViewModelTests
{
    private static readonly Guid NodeA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid NodeB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task RefreshDuringNodeInitializationDoesNotSkipOpeningHistoryAndDraft()
    {
        var directory = new FakeDirectoryReader();
        directory.Set(NodeA, ConversationDirectorySection.ChatContacts,
            Entry(NodeA, "chat", ConversationDirectorySection.ChatContacts, Guid.NewGuid()));
        var dispatcher = new QueuedUiDispatcher();
        var model = Create(directory, dispatcher: dispatcher);
        var load = model.LoadNodeAsync(NodeA, CancellationToken);
        await WaitUntilAsync(() => dispatcher.PendingCount == 1);
        dispatcher.RunNext(); // Projection selected, but History.Open is still queued.
        await WaitUntilAsync(() => dispatcher.PendingCount == 1);
        var refresh = model.RefreshAsync(CancellationToken);
        Assert.False(refresh.IsCompleted);
        Assert.Equal(1, dispatcher.PendingCount);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!load.IsCompleted || !refresh.IsCompleted)
        {
            if (dispatcher.PendingCount > 0) dispatcher.RunNext();
            else await Task.Delay(1, timeout.Token);
        }
        await Task.WhenAll(load, refresh);
        Assert.True(model.History.HasConversation);
        Assert.True(model.Draft.CanEdit);
        Assert.Equal("chat", model.SelectedConversation?.StableKey);
        await model.StopAsync();
    }

    [Fact]
    public async Task ShutdownCancelsRefreshWaitingForNodeInitialization()
    {
        var directory = new FakeDirectoryReader();
        directory.Gates[(NodeA, ConversationDirectorySection.ChatContacts)] =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = Create(directory);
        var load = model.LoadNodeAsync(NodeA, CancellationToken);
        var refresh = model.RefreshAsync(CancellationToken);
        Assert.False(load.IsCompleted);
        Assert.False(refresh.IsCompleted);
        await model.StopAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
    }

    [Fact]
    public void TechnicalListResetDoesNotBecomeConversationDeselection()
    {
        Assert.Null(MainWindow.ConversationSelectionToApply(null));
        Assert.Null(MainWindow.ConversationSelectionToApply(new object()));
    }

    [Fact]
    public void ConversationItemExposesAndUpdatesBoundedUnreadBadge()
    {
        var item = new ConversationListItem(Entry(
            NodeA,
            "unread",
            ConversationDirectorySection.UnknownContacts,
            Guid.NewGuid(),
            unreadCount: 125));

        Assert.True(item.HasUnreadMessages);
        Assert.Equal("99+", item.UnreadLabel);
        item.ApplyUnreadCount(0);
        Assert.False(item.HasUnreadMessages);
        Assert.Equal("0", item.UnreadLabel);
    }

    [Fact]
    public async Task TabsKeepChatServiceChannelAndUnknownEntriesInTheirGroups()
    {
        var directory = new FakeDirectoryReader();
        directory.Set(NodeA, ConversationDirectorySection.ChatContacts, Entry(NodeA, "chat", ConversationDirectorySection.ChatContacts));
        directory.Set(NodeA, ConversationDirectorySection.ServiceContacts, Entry(NodeA, "repeater", ConversationDirectorySection.ServiceContacts, contactType: 2));
        directory.Set(NodeA, ConversationDirectorySection.UnknownContacts, Entry(NodeA, "unknown", ConversationDirectorySection.UnknownContacts));
        directory.Set(NodeA, ConversationDirectorySection.Channels,
            Entry(NodeA, "public", ConversationDirectorySection.Channels, access: ChannelAccessKind.PublicOrHashtag),
            Entry(NodeA, "secret", ConversationDirectorySection.Channels, access: ChannelAccessKind.SharedSecret));
        var model = Create(directory);

        await model.LoadNodeAsync(NodeA, CancellationToken, dispatchResult: false);
        Assert.Equal(["chat"], model.PrimaryConversations.Select(item => item.StableKey));
        Assert.Equal(["unknown"], model.UnknownConversations.Select(item => item.StableKey));
        Assert.DoesNotContain(model.Conversations, item => item.StableKey == "repeater");

        await model.SelectTabAsync(model.Tabs.Single(item => item.Tab == MessengerNavigationTab.Devices), CancellationToken);
        Assert.Equal(["repeater"], model.Conversations.Select(item => item.StableKey));

        await model.SelectTabAsync(model.Tabs.Single(item => item.Tab == MessengerNavigationTab.Channels), CancellationToken);
        model.SelectedChannelFilter = model.ChannelFilters.Single(
            item => item.Filter == ChannelAccessFilter.SharedSecret);
        Assert.Equal(["secret"], model.PrimaryConversations.Select(item => item.StableKey));
        await model.StopAsync();
    }

    [Fact]
    public async Task DirectoryOnlyEntryShowsEmptyHistoryWithoutCreatingOrReadingConversation()
    {
        var directory = new FakeDirectoryReader();
        directory.Set(NodeA, ConversationDirectorySection.ChatContacts,
            Entry(NodeA, "directory-only", ConversationDirectorySection.ChatContacts, conversationId: null));
        var history = new FakeHistoryReader();
        var model = Create(directory, history);

        await model.LoadNodeAsync(NodeA, CancellationToken, dispatchResult: false);

        Assert.Equal("directory-only", model.SelectedConversation?.StableKey);
        Assert.Empty(model.Messages);
        Assert.Empty(history.Requests);
        await model.StopAsync();
    }

    [Fact]
    public async Task RefreshPreservesStableSelectionAcrossRenameAndReorder()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var directory = new FakeDirectoryReader();
        directory.Set(NodeA, ConversationDirectorySection.ChatContacts,
            Entry(NodeA, "first", ConversationDirectorySection.ChatContacts, firstId, "First", sequence: 2),
            Entry(NodeA, "second", ConversationDirectorySection.ChatContacts, secondId, "Second", sequence: 1));
        var model = Create(directory);
        await model.LoadNodeAsync(NodeA, CancellationToken, dispatchResult: false);
        await model.SelectConversationAsync(model.Conversations.Single(item => item.StableKey == "second"), CancellationToken);

        directory.Set(NodeA, ConversationDirectorySection.ChatContacts,
            Entry(NodeA, "second", ConversationDirectorySection.ChatContacts, secondId, "Renamed", sequence: 5),
            Entry(NodeA, "first", ConversationDirectorySection.ChatContacts, firstId, "First", sequence: 2));
        await model.RefreshAsync(CancellationToken);

        Assert.Equal("second", model.SelectedConversation?.StableKey);
        Assert.Equal("Renamed", model.SelectedConversation?.Title);
        await model.StopAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(MessengerNavigationTab.Personal)]
    [InlineData(MessengerNavigationTab.Channels)]
    public async Task LateNodeLoadCannotOverwriteNewNode(MessengerNavigationTab? fixedTab)
    {
        var directory = new FakeDirectoryReader();
        var section = fixedTab == MessengerNavigationTab.Channels ? ConversationDirectorySection.Channels : ConversationDirectorySection.ChatContacts;
        directory.Set(NodeA, section, Entry(NodeA, "a", section));
        directory.Set(NodeB, section, Entry(NodeB, "b", section));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        directory.Gates[(NodeA, section)] = gate;
        var model = Create(directory, fixedTab: fixedTab);

        var slowA = model.LoadNodeAsync(NodeA, CancellationToken);
        await directory.WaitForReadAsync(NodeA, section, CancellationToken);
        await model.LoadNodeAsync(NodeB, CancellationToken);
        gate.SetResult();
        await slowA;

        Assert.Equal(["b"], model.Conversations.Select(item => item.StableKey));
        Assert.All(model.Conversations, item => Assert.Equal(NodeB, item.NodeId));
        await model.StopAsync();
    }

    [Fact]
    public async Task LastTabAndConversationAreRestoredSeparatelyForEachNode()
    {
        var directory = new FakeDirectoryReader();
        directory.Set(NodeA, ConversationDirectorySection.Channels,
            Entry(NodeA, "a-channel", ConversationDirectorySection.Channels));
        directory.Set(NodeB, ConversationDirectorySection.ServiceContacts,
            Entry(NodeB, "b-device", ConversationDirectorySection.ServiceContacts, contactType: 4));
        var settings = new FakeSettingsStore();
        var first = Create(directory, settings: settings);
        await first.LoadNodeAsync(NodeA, CancellationToken, dispatchResult: false);
        await first.SelectTabAsync(first.Tabs.Single(item => item.Tab == MessengerNavigationTab.Channels), CancellationToken);
        await first.LoadNodeAsync(NodeB, CancellationToken, dispatchResult: false);
        await first.SelectTabAsync(first.Tabs.Single(item => item.Tab == MessengerNavigationTab.Devices), CancellationToken);
        await first.StopAsync();

        var restored = Create(directory, settings: settings);
        await restored.LoadNodeAsync(NodeA, CancellationToken, dispatchResult: false);
        Assert.Equal(MessengerNavigationTab.Channels, restored.SelectedTab.Tab);
        Assert.Equal("a-channel", restored.SelectedConversation?.StableKey);
        await restored.LoadNodeAsync(NodeB, CancellationToken, dispatchResult: false);
        Assert.Equal(MessengerNavigationTab.Devices, restored.SelectedTab.Tab);
        Assert.Equal("b-device", restored.SelectedConversation?.StableKey);
        await restored.StopAsync();
    }

    [Fact]
    public async Task RefreshMakesDirectoryOnlySyncVisibleWithoutMessageCommit()
    {
        var directory = new FakeDirectoryReader();
        var model = Create(directory);
        await model.LoadNodeAsync(NodeA, CancellationToken, dispatchResult: false);
        Assert.Empty(model.Conversations);

        directory.Set(NodeA, ConversationDirectorySection.ChatContacts,
            Entry(NodeA, "new-contact", ConversationDirectorySection.ChatContacts));
        await model.RefreshAsync(CancellationToken);

        Assert.Equal("new-contact", Assert.Single(model.Conversations).StableKey);
        await model.StopAsync();
    }

    [Fact]
    public async Task BackgroundRefreshMutatesObservableCollectionsOnlyThroughDispatcher()
    {
        var directory = new FakeDirectoryReader();
        directory.Set(NodeA, ConversationDirectorySection.ChatContacts,
            Entry(NodeA, "first", ConversationDirectorySection.ChatContacts));
        var dispatcher = new QueuedUiDispatcher();
        var model = Create(directory, dispatcher: dispatcher);
        await model.LoadNodeAsync(NodeA, CancellationToken, dispatchResult: false);
        directory.Set(NodeA, ConversationDirectorySection.ChatContacts,
            Entry(NodeA, "second", ConversationDirectorySection.ChatContacts));

        var refresh = model.RefreshAsync(CancellationToken);
        await WaitUntilAsync(() => dispatcher.PendingCount == 1);
        Assert.Equal("first", Assert.Single(model.Conversations).StableKey);
        while (true)
        {
            await WaitUntilAsync(() => dispatcher.PendingCount > 0 || refresh.IsCompleted);
            if (dispatcher.PendingCount > 0)
            {
                dispatcher.RunNext();
                continue;
            }

            break;
        }
        await refresh;

        Assert.Equal("second", Assert.Single(model.Conversations).StableKey);
        await model.StopAsync();
    }

    [Fact]
    public async Task NarrowLayoutUsesExplicitBackNavigation()
    {
        var directory = new FakeDirectoryReader();
        directory.Set(NodeA, ConversationDirectorySection.ChatContacts,
            Entry(NodeA, "chat", ConversationDirectorySection.ChatContacts));
        var model = Create(directory);
        await model.LoadNodeAsync(NodeA, CancellationToken, dispatchResult: false);

        model.SetNarrowLayout(true);
        await model.SelectConversationAsync(null, CancellationToken);
        await model.SelectConversationAsync(model.Conversations[0], CancellationToken);
        Assert.False(model.IsDirectoryVisible);
        Assert.True(model.IsDetailVisible);
        Assert.True(model.CanNavigateBack);

        model.BackCommand.Execute(null);
        Assert.True(model.IsDirectoryVisible);
        Assert.False(model.IsDetailVisible);
        await model.StopAsync();
    }

    [Fact]
    public async Task DirectorySearchUsesCurrentNodeAndTabAndTreatsMetacharactersLiterally()
    {
        var directory = new FakeDirectoryReader();
        directory.Set(NodeA, ConversationDirectorySection.ChatContacts,
            Entry(NodeA, "matching", ConversationDirectorySection.ChatContacts, name: "Кот 🐈 100%_one"),
            Entry(NodeA, "case", ConversationDirectorySection.ChatContacts, name: "кот 🐈 100%_two"),
            Entry(NodeA, "other", ConversationDirectorySection.ChatContacts, name: "Other"));
        directory.Set(NodeB, ConversationDirectorySection.ChatContacts,
            Entry(NodeB, "foreign", ConversationDirectorySection.ChatContacts, name: "Кот 🐈 100%_foreign"));
        var model = Create(directory);
        await model.LoadNodeAsync(NodeA, CancellationToken, dispatchResult: false);

        model.DirectorySearchText = "Кот 🐈 100%_";
        await WaitUntilAsync(() => !model.IsDirectorySearching);

        Assert.Equal("matching", Assert.Single(model.Conversations).StableKey);
        Assert.All(model.Conversations, item => Assert.Equal(NodeA, item.NodeId));
        Assert.Contains("Совпадений: 1", model.DirectorySearchStatus, StringComparison.Ordinal);

        model.DirectorySearchText = string.Empty;
        Assert.Equal(3, model.Conversations.Count);
        await model.StopAsync();
    }

    [Fact]
    public async Task RapidDirectoryQueryCannotApplyAnOlderQueuedResult()
    {
        var directory = new FakeDirectoryReader();
        directory.Set(NodeA, ConversationDirectorySection.ChatContacts,
            Entry(NodeA, "first", ConversationDirectorySection.ChatContacts, name: "First match"),
            Entry(NodeA, "second", ConversationDirectorySection.ChatContacts, name: "Second match"));
        var dispatcher = new QueuedUiDispatcher();
        var model = Create(directory, dispatcher: dispatcher);
        await model.LoadNodeAsync(NodeA, CancellationToken, dispatchResult: false);

        model.DirectorySearchText = "First";
        await WaitUntilAsync(() => dispatcher.PendingCount == 1);
        model.DirectorySearchText = "Second";
        await WaitUntilAsync(() => dispatcher.PendingCount == 2);
        dispatcher.RunNext();
        dispatcher.RunNext();
        await WaitUntilAsync(() => !model.IsDirectorySearching);

        Assert.Equal("second", Assert.Single(model.Conversations).StableKey);
        await model.StopAsync();
    }

    [Fact]
    public async Task FixedWorkspacesPersistIndependentSelectionsWithoutOverwritingLegacyOrLastTab()
    {
        var directory = new FakeDirectoryReader();
        directory.Set(NodeA, ConversationDirectorySection.Channels,
            Entry(NodeA, "c1", ConversationDirectorySection.Channels), Entry(NodeA, "c2", ConversationDirectorySection.Channels));
        directory.Set(NodeA, ConversationDirectorySection.ChatContacts,
            Entry(NodeA, "p1", ConversationDirectorySection.ChatContacts), Entry(NodeA, "p2", ConversationDirectorySection.ChatContacts));
        var settings = new FakeSettingsStore();
        settings.Values[ConversationNavigationViewModel.TabSettingKey(NodeA)] = MessengerNavigationTab.Channels.ToString();
        settings.Values[ConversationNavigationViewModel.ConversationSettingKey(NodeA)] = "c2";
        settings.Values[ConversationNavigationViewModel.ConversationSettingKey(NodeA, MessengerNavigationTab.Personal)] = "p2";
        var channels = Create(directory, settings: settings, fixedTab: MessengerNavigationTab.Channels);
        var personal = Create(directory, settings: settings, fixedTab: MessengerNavigationTab.Personal);
        await channels.LoadNodeAsync(NodeA, CancellationToken, dispatchResult: false);
        await personal.LoadNodeAsync(NodeA, CancellationToken, dispatchResult: false);
        Assert.Equal("c2", channels.SelectedConversation?.StableKey);
        Assert.Equal("p2", personal.SelectedConversation?.StableKey);
        await channels.SelectConversationAsync(channels.Conversations[0], CancellationToken);
        await personal.SelectConversationAsync(personal.Conversations[0], CancellationToken);
        Assert.Equal("c2", settings.Values[ConversationNavigationViewModel.ConversationSettingKey(NodeA)]);
        Assert.Equal("Channels", settings.Values[ConversationNavigationViewModel.TabSettingKey(NodeA)]);
        await channels.StopAsync();
        await personal.StopAsync();
        var restoredChannels = Create(directory, settings: settings, fixedTab: MessengerNavigationTab.Channels);
        var restoredPersonal = Create(directory, settings: settings, fixedTab: MessengerNavigationTab.Personal);
        await restoredChannels.LoadNodeAsync(NodeA, CancellationToken, dispatchResult: false);
        await restoredPersonal.LoadNodeAsync(NodeA, CancellationToken, dispatchResult: false);
        Assert.Equal("c1", restoredChannels.SelectedConversation?.StableKey);
        Assert.Equal("p1", restoredPersonal.SelectedConversation?.StableKey);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restoredChannels.SelectTabAsync(restoredChannels.Tabs[0], CancellationToken));
        await restoredChannels.StopAsync();
        await restoredPersonal.StopAsync();
    }

    [Theory]
    [InlineData(MessengerNavigationTab.Personal)]
    [InlineData(MessengerNavigationTab.Channels)]
    public async Task LegacySelectionFallbackIsUsedOnlyForItsOriginalTab(MessengerNavigationTab savedTab)
    {
        var directory = new FakeDirectoryReader();
        foreach (var section in new[] { ConversationDirectorySection.Channels, ConversationDirectorySection.ChatContacts })
            directory.Set(NodeA, section, Entry(NodeA, "first", section), Entry(NodeA, "second", section));
        var settings = new FakeSettingsStore();
        settings.Values[ConversationNavigationViewModel.TabSettingKey(NodeA)] = savedTab.ToString();
        settings.Values[ConversationNavigationViewModel.ConversationSettingKey(NodeA)] = "second";
        foreach (var tab in new[] { MessengerNavigationTab.Personal, MessengerNavigationTab.Channels })
        {
            var model = Create(directory, settings: settings, fixedTab: tab);
            await model.LoadNodeAsync(NodeA, CancellationToken, dispatchResult: false);
            Assert.Equal(tab == savedTab ? "second" : "first", model.SelectedConversation?.StableKey);
            await model.StopAsync();
        }
    }

    private static ConversationNavigationViewModel Create(
        FakeDirectoryReader directory,
        FakeHistoryReader? history = null,
        FakeSettingsStore? settings = null,
        IUiDispatcher? dispatcher = null,
        MessengerNavigationTab? fixedTab = null)
    {
        var readStates = new FakeConversationReadStateService();
        return new(
            directory,
            history ?? new FakeHistoryReader(),
            readStates,
            readStates,
            new FakeDraftBuffer(),
            settings ?? new FakeSettingsStore(),
            dispatcher ?? new ImmediateUiDispatcher(),
            new ImmediateSearchDelay(),
            new ImmediateDraftDelay(),
            NullLogger.Instance, fixedTab);
    }

    private static ConversationDirectoryEntry Entry(
        Guid nodeId,
        string key,
        ConversationDirectorySection section,
        Guid? conversationId = null,
        string? name = null,
        int? contactType = 1,
        ChannelAccessKind? access = null,
        long sequence = 1,
        long unreadCount = 0) =>
        new(
            nodeId,
            key,
            section,
            section switch
            {
                ConversationDirectorySection.Channels => ConversationKind.Channel,
                ConversationDirectorySection.UnknownChannels => ConversationKind.UnknownChannel,
                ConversationDirectorySection.UnknownContacts => ConversationKind.UnknownContact,
                _ => ConversationKind.Contact,
            },
            System.Text.Encoding.UTF8.GetBytes(key),
            conversationId,
            name ?? key,
            contactType,
            true,
            access,
            [],
            false,
            DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            sequence,
            DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            null,
            null,
            null,
            null,
            null,
            null,
            unreadCount);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class FakeDirectoryReader : IConversationDirectoryReader
    {
        private readonly Dictionary<(Guid, ConversationDirectorySection), IReadOnlyList<ConversationDirectoryEntry>> _entries = [];
        private readonly Dictionary<(Guid, ConversationDirectorySection), int> _reads = [];
        public Dictionary<(Guid, ConversationDirectorySection), TaskCompletionSource> Gates { get; } = [];

        public void Set(Guid nodeId, ConversationDirectorySection section, params ConversationDirectoryEntry[] entries) =>
            _entries[(nodeId, section)] = entries;

        public async Task<ConversationDirectoryPage> GetPageAsync(
            Guid nodeId,
            ConversationDirectorySection section,
            ConversationDirectoryCursor? after,
            int limit,
            CancellationToken cancellationToken = default)
        {
            var key = (nodeId, section);
            _reads[key] = _reads.GetValueOrDefault(key) + 1;
            if (Gates.TryGetValue(key, out var gate))
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            var items = _entries.GetValueOrDefault(key) ?? [];
            return new ConversationDirectoryPage(items.Take(limit).ToArray(), null);
        }

        public Task<ConversationDirectoryPage> SearchPageAsync(
            Guid nodeId,
            ConversationDirectorySection section,
            string query,
            ConversationDirectoryCursor? after,
            int limit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = (_entries.GetValueOrDefault((nodeId, section)) ?? [])
                .Where(item => item.DisplayName?.Contains(query, StringComparison.Ordinal) == true)
                .Take(limit)
                .ToArray();
            return Task.FromResult(new ConversationDirectoryPage(items, null));
        }

        public Task<ContactDetailsProjection?> GetContactDetailsAsync(
            Guid nodeId,
            ReadOnlyMemory<byte> publicKey,
            CancellationToken cancellationToken = default) => Task.FromResult<ContactDetailsProjection?>(null);

        public Task<ChannelDetailsProjection?> GetChannelDetailsAsync(
            Guid nodeId,
            ReadOnlyMemory<byte> keyFingerprint,
            CancellationToken cancellationToken = default) => Task.FromResult<ChannelDetailsProjection?>(null);

        public async Task WaitForReadAsync(
            Guid nodeId,
            ConversationDirectorySection section,
            CancellationToken cancellationToken)
        {
            while (_reads.GetValueOrDefault((nodeId, section)) == 0)
            {
                await Task.Delay(10, cancellationToken);
            }
        }
    }

    private sealed class FakeHistoryReader : ILocalHistoryReader
    {
        public List<(Guid NodeId, Guid ConversationId)> Requests { get; } = [];

        public Task<IReadOnlyList<ConversationSummary>> GetConversationsAsync(
            Guid nodeId,
            int limit,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("C3 navigation must use the directory projection.");

        public Task<IReadOnlyList<HistoryMessage>> GetMessagesAsync(
            Guid nodeId,
            Guid conversationId,
            long? beforeLocalSequence,
            int limit,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((nodeId, conversationId));
            return Task.FromResult<IReadOnlyList<HistoryMessage>>([]);
        }

        public Task<HistoryMessagePosition?> GetMessagePositionAsync(
            Guid nodeId, Guid conversationId, Guid messageId,
            CancellationToken cancellationToken = default) => Task.FromResult<HistoryMessagePosition?>(null);

        public Task<HistoryMessagePage> GetMessagesBeforeAsync(
            Guid nodeId, Guid conversationId, HistoryMessagePosition? before, int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryMessagePage([], null, null, false, false));

        public Task<HistoryMessagePage> GetMessagesAfterAsync(
            Guid nodeId, Guid conversationId, HistoryMessagePosition? after, int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryMessagePage([], null, null, false, false));

        public Task<HistoryMessagePage> GetMessagesAroundAsync(
            HistoryMessagePosition position, int beforeLimit, int afterLimit,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<HistoryMessageSearchPage> SearchMessagesAsync(
            Guid nodeId, Guid conversationId, string query, HistoryMessagePosition? before, int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryMessageSearchPage([], null));
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }
    }
}
