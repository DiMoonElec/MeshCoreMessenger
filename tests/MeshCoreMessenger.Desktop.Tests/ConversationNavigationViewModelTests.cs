using Microsoft.Extensions.Logging.Abstractions;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class ConversationNavigationViewModelTests
{
    private static readonly Guid NodeA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid NodeB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

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

    [Fact]
    public async Task LateNodeLoadCannotOverwriteNewNode()
    {
        var directory = new FakeDirectoryReader();
        directory.Set(NodeA, ConversationDirectorySection.ChatContacts,
            Entry(NodeA, "a", ConversationDirectorySection.ChatContacts));
        directory.Set(NodeB, ConversationDirectorySection.ChatContacts,
            Entry(NodeB, "b", ConversationDirectorySection.ChatContacts));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        directory.Gates[(NodeA, ConversationDirectorySection.ChatContacts)] = gate;
        var model = Create(directory);

        var slowA = model.LoadNodeAsync(NodeA, CancellationToken);
        await directory.WaitForReadAsync(NodeA, ConversationDirectorySection.ChatContacts, CancellationToken);
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
        dispatcher.RunNext();
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

    private static ConversationNavigationViewModel Create(
        FakeDirectoryReader directory,
        FakeHistoryReader? history = null,
        FakeSettingsStore? settings = null,
        IUiDispatcher? dispatcher = null) =>
        new(
            directory,
            history ?? new FakeHistoryReader(),
            settings ?? new FakeSettingsStore(),
            dispatcher ?? new ImmediateUiDispatcher(),
            NullLogger.Instance);

    private static ConversationDirectoryEntry Entry(
        Guid nodeId,
        string key,
        ConversationDirectorySection section,
        Guid? conversationId = null,
        string? name = null,
        int? contactType = 1,
        ChannelAccessKind? access = null,
        long sequence = 1) =>
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
            null);

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
