using Microsoft.Extensions.Logging.Abstractions;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class MainWindowViewModelTests
{
    private static readonly Guid NodeAId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid NodeBId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task StartupWithoutKnownNodeShowsExplicitEmptySelection()
    {
        var viewModel = CreateViewModel(
            new FakeHistoryReader(),
            nodes: new FakeNodeStore([]),
            settings: new FakeSettingsStore());

        await viewModel.LoadAsync(CancellationToken);

        Assert.Empty(viewModel.KnownNodes);
        Assert.Null(viewModel.ViewedNode);
        Assert.Empty(viewModel.Conversations);
        Assert.Equal("Выберите ноду для просмотра истории", viewModel.Status);
        Assert.Equal("Не подключено", viewModel.ConnectionStatus);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task RestoresViewedNodeAndOfflineHistoryFromSettings()
    {
        var conversationId = Guid.NewGuid();
        var history = new FakeHistoryReader();
        history.SetConversations(NodeBId, [CreateSummary(NodeBId, conversationId, "B history", 1)]);
        history.Messages[conversationId] = [CreateMessage(conversationId, 1, "offline body")];
        var settings = CreateSettings(NodeBId, follow: false);
        var viewModel = CreateViewModel(history, nodes: CreateTwoNodes(), settings: settings);

        await viewModel.LoadAsync(CancellationToken);

        Assert.Equal(NodeBId, viewModel.ViewedNode?.Id);
        Assert.Equal(conversationId, Assert.Single(viewModel.Conversations).Id);
        Assert.Equal("offline body", Assert.Single(viewModel.Messages).Body);
        Assert.Null(viewModel.ActiveNode);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task InitialOfflineLoadDoesNotWaitForUiDispatcherLoop()
    {
        var conversationId = Guid.NewGuid();
        var history = new FakeHistoryReader();
        history.SetConversations(NodeAId, [CreateSummary(NodeAId, conversationId, "Offline", 1)]);
        var dispatcher = new QueuedUiDispatcher();
        var viewModel = CreateViewModel(history, dispatcher: dispatcher);

        await viewModel.LoadAsync(CancellationToken).WaitAsync(TimeSpan.FromSeconds(1), CancellationToken);

        Assert.Equal(conversationId, Assert.Single(viewModel.Conversations).Id);
        Assert.Equal(0, dispatcher.PendingCount);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task SelectionPersistsAcrossViewModelRestart()
    {
        var settings = CreateSettings(NodeAId, follow: false);
        var nodes = CreateTwoNodes();
        var first = CreateViewModel(new FakeHistoryReader(), nodes: nodes, settings: settings);
        await first.LoadAsync(CancellationToken);

        await first.SelectViewedNodeAsync(first.KnownNodes.Single(item => item.Id == NodeBId), CancellationToken);
        await first.StopAsync();

        var second = CreateViewModel(new FakeHistoryReader(), nodes: nodes, settings: settings);
        await second.LoadAsync(CancellationToken);

        Assert.Equal(NodeBId, second.ViewedNode?.Id);
        Assert.False(second.IsFollowingActiveNode);
        await second.StopAsync();
    }

    [Fact]
    public async Task LoadsSelectedNodeConversationsAndCanSelectAnotherConversation()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var history = new FakeHistoryReader();
        history.SetConversations(
            NodeAId,
            [
                CreateSummary(NodeAId, firstId, "First", 2),
                CreateSummary(NodeAId, secondId, "Second", 1),
            ]);
        history.Messages[firstId] = [CreateMessage(firstId, 2, "first body")];
        history.Messages[secondId] = [CreateMessage(secondId, 1, "second body")];
        var viewModel = CreateViewModel(history);

        await viewModel.LoadAsync(CancellationToken);
        await viewModel.SelectConversationAsync(viewModel.Conversations[1], CancellationToken);

        Assert.Equal(secondId, viewModel.SelectedConversation?.Id);
        Assert.Equal("second body", Assert.Single(viewModel.Messages).Body);
        Assert.Equal([firstId, secondId], history.RequestedConversations);
        Assert.All(history.RequestedNodes, nodeId => Assert.Equal(NodeAId, nodeId));
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task SlowNodeAResultCannotOverwriteNodeBAfterSelectionChanges()
    {
        var conversationA = Guid.NewGuid();
        var conversationB = Guid.NewGuid();
        var history = new FakeHistoryReader();
        history.SetConversations(NodeAId, [CreateSummary(NodeAId, conversationA, "A", 1)]);
        history.SetConversations(NodeBId, [CreateSummary(NodeBId, conversationB, "B", 2)]);
        history.Messages[conversationA] = [CreateMessage(conversationA, 1, "A body")];
        history.Messages[conversationB] = [CreateMessage(conversationB, 2, "B body")];
        var viewModel = CreateViewModel(history, nodes: CreateTwoNodes());
        await viewModel.LoadAsync(CancellationToken);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        history.ConversationGates[NodeAId] = gate;

        var slowA = viewModel.SelectViewedNodeAsync(
            viewModel.KnownNodes.Single(item => item.Id == NodeAId),
            CancellationToken);
        await WaitUntilAsync(() => history.GetConversationReads(NodeAId) >= 2);
        await viewModel.SelectViewedNodeAsync(
            viewModel.KnownNodes.Single(item => item.Id == NodeBId),
            CancellationToken);
        gate.SetResult();
        await slowA;

        Assert.Equal(NodeBId, viewModel.ViewedNode?.Id);
        Assert.Equal(conversationB, Assert.Single(viewModel.Conversations).Id);
        Assert.Equal("B body", Assert.Single(viewModel.Messages).Body);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task OldNodeCommitDoesNotReloadOrEnterViewedNodeProjection()
    {
        var history = new FakeHistoryReader();
        var notifications = new FakeMessageCommitNotifications();
        var viewModel = CreateViewModel(history, notifications: notifications, nodes: CreateTwoNodes());
        await viewModel.LoadAsync(CancellationToken);
        await viewModel.SelectViewedNodeAsync(
            viewModel.KnownNodes.Single(item => item.Id == NodeBId),
            CancellationToken);
        var reads = history.TotalConversationReads;

        notifications.Publish(CreateCommit(NodeAId, inserted: true));
        await Task.Delay(30, CancellationToken);

        Assert.Equal(reads, history.TotalConversationReads);
        Assert.Equal(NodeBId, viewModel.ViewedNode?.Id);
        Assert.Empty(viewModel.Conversations);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task MatchingCommittedMessageRereadsCommittedProjection()
    {
        var conversationId = Guid.NewGuid();
        var history = new FakeHistoryReader();
        var notifications = new FakeMessageCommitNotifications();
        var viewModel = CreateViewModel(history, notifications: notifications);
        await viewModel.LoadAsync(CancellationToken);
        var reads = history.TotalConversationReads;
        history.SetConversations(NodeAId, [CreateSummary(NodeAId, conversationId, "New", 1)]);
        history.Messages[conversationId] = [CreateMessage(conversationId, 1, "committed body")];

        notifications.Publish(CreateCommit(NodeAId, inserted: true, conversationId));

        await WaitUntilAsync(() => viewModel.Messages.Count == 1);
        Assert.True(history.TotalConversationReads > reads);
        Assert.Equal("committed body", Assert.Single(viewModel.Messages).Body);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task DuplicateCommitDoesNotReloadProjection()
    {
        var history = new FakeHistoryReader();
        var notifications = new FakeMessageCommitNotifications();
        var viewModel = CreateViewModel(history, notifications: notifications);
        await viewModel.LoadAsync(CancellationToken);
        var reads = history.TotalConversationReads;

        notifications.Publish(CreateCommit(NodeAId, inserted: false));
        await Task.Delay(30, CancellationToken);

        Assert.Equal(reads, history.TotalConversationReads);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task FollowingConnectionHandlesAtoBtoAOnOneProfileWithoutMixingHistory()
    {
        var profileId = Guid.NewGuid();
        var conversationA = Guid.NewGuid();
        var conversationB = Guid.NewGuid();
        var history = new FakeHistoryReader();
        history.SetConversations(NodeAId, [CreateSummary(NodeAId, conversationA, "A", 1)]);
        history.SetConversations(NodeBId, [CreateSummary(NodeBId, conversationB, "B", 2)]);
        history.Messages[conversationA] = [CreateMessage(conversationA, 1, "A history")];
        history.Messages[conversationB] = [CreateMessage(conversationB, 2, "B history")];
        var supervisor = new FakeConnectionSupervisor();
        var settings = new FakeSettingsStore();
        var viewModel = CreateViewModel(
            history,
            supervisor,
            nodes: CreateTwoNodes(),
            settings: settings,
            profileManager: new TestProfileManager([CreateProfile(profileId, "Shared endpoint")]));
        await viewModel.LoadAsync(CancellationToken);

        supervisor.Publish(CreateSnapshot(ConnectionSupervisorState.Online, NodeAId, profileId));
        await WaitUntilAsync(() => viewModel.ViewedNode?.Id == NodeAId && viewModel.Messages.Count == 1);
        Assert.Equal("A history", Assert.Single(viewModel.Messages).Body);

        supervisor.Publish(CreateSnapshot(ConnectionSupervisorState.Online, NodeBId, profileId, generation: 2));
        await WaitUntilAsync(() => viewModel.ViewedNode?.Id == NodeBId && viewModel.Messages.Count == 1);
        Assert.Equal("B history", Assert.Single(viewModel.Messages).Body);

        supervisor.Publish(CreateSnapshot(ConnectionSupervisorState.Online, NodeAId, profileId, generation: 3));
        await WaitUntilAsync(() => viewModel.ViewedNode?.Id == NodeAId && viewModel.Messages.Count == 1);
        Assert.Equal("A history", Assert.Single(viewModel.Messages).Body);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task SameNodeThroughDifferentProfilesKeepsOneIdentityAndHistory()
    {
        var serialProfile = CreateProfile(Guid.NewGuid(), "Serial");
        var tcpProfile = CreateProfile(Guid.NewGuid(), "TCP");
        var conversation = Guid.NewGuid();
        var history = new FakeHistoryReader();
        history.SetConversations(NodeAId, [CreateSummary(NodeAId, conversation, "A", 1)]);
        var supervisor = new FakeConnectionSupervisor();
        var viewModel = CreateViewModel(
            history,
            supervisor,
            profileManager: new TestProfileManager([serialProfile, tcpProfile]));
        await viewModel.LoadAsync(CancellationToken);

        supervisor.Publish(CreateSnapshot(ConnectionSupervisorState.Online, NodeAId, serialProfile.Id));
        await WaitUntilAsync(() => viewModel.ActiveProfileDisplayName.Contains("Serial", StringComparison.Ordinal));
        var firstViewedId = viewModel.ViewedNode?.Id;
        supervisor.Publish(CreateSnapshot(ConnectionSupervisorState.Online, NodeAId, tcpProfile.Id, generation: 2));
        await WaitUntilAsync(() => viewModel.ActiveProfileDisplayName.Contains("TCP", StringComparison.Ordinal));

        Assert.Equal(NodeAId, firstViewedId);
        Assert.Equal(NodeAId, viewModel.ViewedNode?.Id);
        Assert.Single(viewModel.Conversations);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task SameNameNodesRemainDistinguishableByFullPublicKey()
    {
        var nodes = new FakeNodeStore(
            [CreateNode(NodeAId, "Same name", 0x11), CreateNode(NodeBId, "Same name", 0x22)]);
        var viewModel = CreateViewModel(new FakeHistoryReader(), nodes: nodes);

        await viewModel.LoadAsync(CancellationToken);

        Assert.Equal(2, viewModel.KnownNodes.Count);
        Assert.All(viewModel.KnownNodes, item => Assert.Contains("Same name", item.SelectorLabel));
        Assert.NotEqual(viewModel.KnownNodes[0].SelectorLabel, viewModel.KnownNodes[1].SelectorLabel);
        Assert.All(viewModel.KnownNodes, item => Assert.Equal(64, item.PublicKeyHex.Length));
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task ProfileEditorSelectionDoesNotChangeActiveAttemptLabel()
    {
        var activeProfile = CreateProfile(Guid.NewGuid(), "Active profile");
        var editedProfile = CreateProfile(Guid.NewGuid(), "Editor only");
        var supervisor = new FakeConnectionSupervisor();
        var viewModel = CreateViewModel(
            new FakeHistoryReader(),
            supervisor,
            profileManager: new TestProfileManager([activeProfile, editedProfile]));
        await viewModel.LoadAsync(CancellationToken);
        supervisor.Publish(CreateSnapshot(ConnectionSupervisorState.Connecting, null, activeProfile.Id));
        await WaitUntilAsync(() => viewModel.ActiveProfileDisplayName.Contains("Active profile", StringComparison.Ordinal));

        viewModel.Profiles.SelectedProfile = viewModel.Profiles.AvailableProfiles.Single(
            item => item.Id == editedProfile.Id);

        Assert.Contains("Active profile", viewModel.ActiveProfileDisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain("Editor only", viewModel.ActiveProfileDisplayName, StringComparison.Ordinal);
        await viewModel.StopAsync();
    }

    [Theory]
    [InlineData(ConnectionSupervisorState.Offline)]
    [InlineData(ConnectionSupervisorState.Identifying)]
    public async Task OfflineAndIdentifyingNeverExposeSnapshotNodeAsActive(ConnectionSupervisorState state)
    {
        var supervisor = new FakeConnectionSupervisor();
        var viewModel = CreateViewModel(new FakeHistoryReader(), supervisor, nodes: CreateTwoNodes());
        await viewModel.LoadAsync(CancellationToken);

        supervisor.Publish(CreateSnapshot(state, NodeAId, Guid.NewGuid()));
        await WaitUntilAsync(() => viewModel.ConnectionStatus ==
            (state == ConnectionSupervisorState.Offline ? "Не подключено" : "Идентификация…"));

        Assert.Null(viewModel.ActiveNode);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task ManualNodeSelectionPinsHistoryUntilFollowCommand()
    {
        var supervisor = new FakeConnectionSupervisor();
        var viewModel = CreateViewModel(new FakeHistoryReader(), supervisor, nodes: CreateTwoNodes());
        await viewModel.LoadAsync(CancellationToken);
        await viewModel.SelectViewedNodeAsync(
            viewModel.KnownNodes.Single(item => item.Id == NodeBId),
            CancellationToken);

        supervisor.Publish(CreateSnapshot(ConnectionSupervisorState.Online, NodeAId, Guid.NewGuid()));
        await WaitUntilAsync(() => viewModel.ActiveNode?.Id == NodeAId);
        Assert.Equal(NodeBId, viewModel.ViewedNode?.Id);

        await viewModel.FollowActiveNodeCommand.ExecuteAsync(null);
        Assert.Equal(NodeAId, viewModel.ViewedNode?.Id);
        Assert.True(viewModel.IsFollowingActiveNode);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task ReadFailureBecomesVisibleShellState()
    {
        var history = new FakeHistoryReader { ConversationError = new IOException("test") };
        var viewModel = CreateViewModel(history);

        await viewModel.LoadAsync(CancellationToken);

        Assert.True(viewModel.HasError);
        Assert.Equal("Ошибка локального хранилища", viewModel.Status);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task CancellationPropagatesAndLeavesConsistentState()
    {
        var history = new FakeHistoryReader { WaitForCancellation = true };
        var viewModel = CreateViewModel(history);
        using var cancellation = new CancellationTokenSource();
        var load = viewModel.LoadAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.False(viewModel.IsLoading);
        Assert.Equal("Загрузка локальной истории отменена", viewModel.Status);
        await viewModel.StopAsync();
    }

    [Theory]
    [InlineData(ConnectionSupervisorState.Offline, "Не подключено")]
    [InlineData(ConnectionSupervisorState.Connecting, "Подключение…")]
    [InlineData(ConnectionSupervisorState.Identifying, "Идентификация…")]
    [InlineData(ConnectionSupervisorState.Synchronizing, "Синхронизация…")]
    [InlineData(ConnectionSupervisorState.Online, "Подключено")]
    [InlineData(ConnectionSupervisorState.RetryWaiting, "Ожидание повтора")]
    [InlineData(ConnectionSupervisorState.Disconnecting, "Отключение…")]
    [InlineData(ConnectionSupervisorState.NeedsAttention, "Требуется внимание")]
    public async Task MapsEverySupervisorState(ConnectionSupervisorState state, string expectedStatus)
    {
        var supervisor = new FakeConnectionSupervisor();
        var viewModel = CreateViewModel(new FakeHistoryReader(), supervisor);
        await viewModel.LoadAsync(CancellationToken);

        supervisor.Publish(CreateSnapshot(state, null, Guid.NewGuid(), reason: "test reason"));
        await WaitUntilAsync(() => viewModel.ConnectionStatus == expectedStatus);

        Assert.Contains("test reason", viewModel.ConnectionStatusDetail, StringComparison.Ordinal);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task SupervisorStateIsAppliedThroughUiDispatcher()
    {
        var supervisor = new FakeConnectionSupervisor();
        var dispatcher = new QueuedUiDispatcher();
        var viewModel = CreateViewModel(new FakeHistoryReader(), supervisor, dispatcher: dispatcher);

        supervisor.Publish(CreateSnapshot(ConnectionSupervisorState.Connecting, null, Guid.NewGuid()));
        await WaitUntilAsync(() => dispatcher.PendingCount == 1);
        Assert.Equal("Не подключено", viewModel.ConnectionStatus);
        dispatcher.RunNext();

        Assert.Equal("Подключение…", viewModel.ConnectionStatus);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task ConnectAndDisconnectCommandsDelegateToSupervisor()
    {
        var supervisor = new FakeConnectionSupervisor();
        var viewModel = CreateViewModel(new FakeHistoryReader(), supervisor);

        await viewModel.ConnectCommand.ExecuteAsync(null);
        await viewModel.DisconnectCommand.ExecuteAsync(null);

        Assert.Equal(1, supervisor.ConnectCalls);
        Assert.Equal(1, supervisor.DisconnectCalls);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task LateCallbacksAfterStopDoNotChangeProjectionOrConnectionState()
    {
        var history = new FakeHistoryReader();
        var supervisor = new FakeConnectionSupervisor();
        var notifications = new FakeMessageCommitNotifications();
        var viewModel = CreateViewModel(history, supervisor, notifications);
        await viewModel.LoadAsync(CancellationToken);
        var reads = history.TotalConversationReads;
        await viewModel.StopAsync();

        supervisor.Publish(CreateSnapshot(ConnectionSupervisorState.Online, NodeAId, Guid.NewGuid()));
        notifications.Publish(CreateCommit(NodeAId, inserted: true));

        Assert.Equal("Не подключено", viewModel.ConnectionStatus);
        Assert.Equal(reads, history.TotalConversationReads);
    }

    [Fact]
    public async Task ShutdownPersistenceFailureRemainsVisibleAfterUiScenariosStop()
    {
        var viewModel = CreateViewModel(new FakeHistoryReader());
        await viewModel.StopAsync();

        await ((IDesktopUiLifetime)viewModel).ReportShutdownFailureAsync(new IOException("disk full"));

        Assert.True(viewModel.HasError);
        Assert.Contains("повторите закрытие", viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    private static MainWindowViewModel CreateViewModel(
        ILocalHistoryReader history,
        FakeConnectionSupervisor? supervisor = null,
        FakeMessageCommitNotifications? notifications = null,
        IUiDispatcher? dispatcher = null,
        FakeNodeStore? nodes = null,
        FakeSettingsStore? settings = null,
        TestProfileManager? profileManager = null)
    {
        supervisor ??= new FakeConnectionSupervisor();
        nodes ??= new FakeNodeStore([CreateNode(NodeAId, "Node A", 0x11)]);
        settings ??= CreateSettings(NodeAId, follow: false);
        var profiles = new ConnectionProfilesViewModel(
            profileManager ?? new TestProfileManager([]),
            supervisor,
            new EmptySerialPortCatalog(),
            NullLogger<ConnectionProfilesViewModel>.Instance);
        return new MainWindowViewModel(
            new FakeConversationDirectoryReader(history),
            history,
            nodes,
            settings,
            profiles,
            supervisor,
            notifications ?? new FakeMessageCommitNotifications(),
            dispatcher ?? new ImmediateUiDispatcher(),
            NullLogger<MainWindowViewModel>.Instance);
    }

    private static FakeNodeStore CreateTwoNodes() => new(
        [CreateNode(NodeAId, "Node A", 0x11), CreateNode(NodeBId, "Node B", 0x22)]);

    private static FakeSettingsStore CreateSettings(Guid nodeId, bool follow)
    {
        var settings = new FakeSettingsStore();
        settings.Values["desktop.viewed-node-id"] = nodeId.ToString("D");
        settings.Values["desktop.follow-active-node"] = follow.ToString();
        return settings;
    }

    private static NodeRecord CreateNode(Guid id, string name, byte keyByte) =>
        new(id, Enumerable.Repeat(keyByte, 32).ToArray(), name, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static ConnectionProfile CreateProfile(Guid id, string name) => new()
    {
        Id = id,
        Name = name,
        Transport = ConnectionTransportKind.Tcp,
        TcpHost = "127.0.0.1",
        TcpPort = 5000,
        CreatedUtc = DateTimeOffset.UtcNow,
        UpdatedUtc = DateTimeOffset.UtcNow,
    };

    private static ConnectionSupervisorSnapshot CreateSnapshot(
        ConnectionSupervisorState state,
        Guid? nodeId,
        Guid profileId,
        long generation = 1,
        string? reason = null) =>
        new(state, generation, profileId, Guid.NewGuid(), nodeId, reason, null);

    private static StoredIncomingMessage CreateCommit(
        Guid nodeId,
        bool inserted,
        Guid? conversationId = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), nodeId, conversationId ?? Guid.NewGuid(), 1, inserted);

    private static ConversationSummary CreateSummary(
        Guid nodeId,
        Guid id,
        string title,
        long sequence) =>
        new(
            id,
            nodeId,
            ConversationKind.UnknownContact,
            title,
            false,
            DateTimeOffset.UtcNow,
            sequence,
            MessageDirection.Incoming,
            StoredMessageKind.Text,
            title,
            DateTimeOffset.UtcNow);

    private static HistoryMessage CreateMessage(Guid conversationId, long sequence, string text) =>
        new(
            Guid.NewGuid(),
            sequence,
            conversationId,
            MessageDirection.Incoming,
            StoredMessageKind.Text,
            text,
            DateTimeOffset.UtcNow);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class FakeHistoryReader : ILocalHistoryReader
    {
        private readonly Dictionary<Guid, IReadOnlyList<ConversationSummary>> _conversations = [];
        private readonly Dictionary<Guid, int> _conversationReads = [];

        public Dictionary<Guid, IReadOnlyList<HistoryMessage>> Messages { get; } = [];
        public Dictionary<Guid, TaskCompletionSource> ConversationGates { get; } = [];
        public List<Guid> RequestedConversations { get; } = [];
        public List<Guid> RequestedNodes { get; } = [];
        public Exception? ConversationError { get; init; }
        public bool WaitForCancellation { get; init; }
        public int TotalConversationReads => _conversationReads.Values.Sum();

        public void SetConversations(Guid nodeId, IReadOnlyList<ConversationSummary> conversations) =>
            _conversations[nodeId] = conversations;

        public int GetConversationReads(Guid nodeId) =>
            _conversationReads.GetValueOrDefault(nodeId);

        public async Task<IReadOnlyList<ConversationSummary>> GetConversationsAsync(
            Guid nodeId,
            int limit,
            CancellationToken cancellationToken = default)
        {
            _conversationReads[nodeId] = GetConversationReads(nodeId) + 1;
            RequestedNodes.Add(nodeId);
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (ConversationError is not null)
            {
                throw ConversationError;
            }

            if (ConversationGates.TryGetValue(nodeId, out var gate))
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            return _conversations.TryGetValue(nodeId, out var conversations)
                ? conversations.Take(limit).ToArray()
                : [];
        }

        public Task<IReadOnlyList<HistoryMessage>> GetMessagesAsync(
            Guid nodeId,
            Guid conversationId,
            long? beforeLocalSequence,
            int limit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedNodes.Add(nodeId);
            RequestedConversations.Add(conversationId);
            IReadOnlyList<HistoryMessage> result = Messages.TryGetValue(conversationId, out var messages)
                ? messages.Take(limit).ToArray()
                : [];
            return Task.FromResult(result);
        }

        public Task<HistoryMessagePosition?> GetMessagePositionAsync(
            Guid nodeId, Guid conversationId, Guid messageId,
            CancellationToken cancellationToken = default)
        {
            var message = Messages.GetValueOrDefault(conversationId)?.FirstOrDefault(item => item.Id == messageId);
            return Task.FromResult(message is null
                ? null
                : new HistoryMessagePosition(nodeId, conversationId, message.Id, message.LocalSequence));
        }

        public Task<HistoryMessagePage> GetMessagesBeforeAsync(
            Guid nodeId, Guid conversationId, HistoryMessagePosition? before, int limit,
            CancellationToken cancellationToken = default)
        {
            RequestedNodes.Add(nodeId);
            RequestedConversations.Add(conversationId);
            return Task.FromResult(Page(
                nodeId,
                conversationId,
                (Messages.GetValueOrDefault(conversationId) ?? [])
                    .Where(item => before is null || item.LocalSequence < before.LocalSequence)
                    .OrderByDescending(item => item.LocalSequence)
                    .Take(limit)
                    .OrderBy(item => item.LocalSequence)
                    .ToArray()));
        }

        public Task<HistoryMessagePage> GetMessagesAfterAsync(
            Guid nodeId, Guid conversationId, HistoryMessagePosition? after, int limit,
            CancellationToken cancellationToken = default)
        {
            RequestedNodes.Add(nodeId);
            RequestedConversations.Add(conversationId);
            return Task.FromResult(Page(
                nodeId,
                conversationId,
                (Messages.GetValueOrDefault(conversationId) ?? [])
                    .Where(item => after is null || item.LocalSequence > after.LocalSequence)
                    .OrderBy(item => item.LocalSequence)
                    .Take(limit)
                    .ToArray()));
        }

        public Task<HistoryMessagePage> GetMessagesAroundAsync(
            HistoryMessagePosition position, int beforeLimit, int afterLimit,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private HistoryMessagePage Page(
            Guid nodeId,
            Guid conversationId,
            IReadOnlyList<HistoryMessage> items)
        {
            var all = Messages.GetValueOrDefault(conversationId) ?? [];
            var first = items.FirstOrDefault();
            var last = items.LastOrDefault();
            return new HistoryMessagePage(
                items,
                first is null ? null : new(nodeId, conversationId, first.Id, first.LocalSequence),
                last is null ? null : new(nodeId, conversationId, last.Id, last.LocalSequence),
                first is not null && all.Any(item => item.LocalSequence < first.LocalSequence),
                last is not null && all.Any(item => item.LocalSequence > last.LocalSequence));
        }
    }

    private sealed class FakeConversationDirectoryReader(ILocalHistoryReader history)
        : IConversationDirectoryReader
    {
        public async Task<ConversationDirectoryPage> GetPageAsync(
            Guid nodeId,
            ConversationDirectorySection section,
            ConversationDirectoryCursor? after,
            int limit,
            CancellationToken cancellationToken = default)
        {
            var summaries = await history.GetConversationsAsync(nodeId, limit, cancellationToken);
            var items = summaries
                .Where(summary => Section(summary.Kind) == section)
                .Select(summary => new ConversationDirectoryEntry(
                    summary.NodeId,
                    summary.Id.ToString("N"),
                    section,
                    summary.Kind,
                    summary.Id.ToByteArray(),
                    summary.Id,
                    summary.Title,
                    null,
                    null,
                    null,
                    [],
                    summary.IsArchived,
                    summary.UpdatedUtc,
                    summary.LastMessageSequence ?? 0,
                    summary.LastMessageUtc ?? summary.UpdatedUtc,
                    summary.LastMessageSequence,
                    summary.LastMessageDirection,
                    summary.LastMessageKind,
                    MessageResolutionState.Resolved,
                    summary.LastMessageText,
                    summary.LastMessageUtc))
                .ToArray();
            return new ConversationDirectoryPage(items, null);
        }

        public Task<ContactDetailsProjection?> GetContactDetailsAsync(
            Guid nodeId,
            ReadOnlyMemory<byte> publicKey,
            CancellationToken cancellationToken = default) => Task.FromResult<ContactDetailsProjection?>(null);

        public Task<ChannelDetailsProjection?> GetChannelDetailsAsync(
            Guid nodeId,
            ReadOnlyMemory<byte> keyFingerprint,
            CancellationToken cancellationToken = default) => Task.FromResult<ChannelDetailsProjection?>(null);

        private static ConversationDirectorySection Section(ConversationKind kind) => kind switch
        {
            ConversationKind.Contact => ConversationDirectorySection.ChatContacts,
            ConversationKind.Channel => ConversationDirectorySection.Channels,
            ConversationKind.UnknownContact => ConversationDirectorySection.UnknownContacts,
            ConversationKind.UnknownChannel => ConversationDirectorySection.UnknownChannels,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private sealed class FakeNodeStore(IReadOnlyList<NodeRecord> nodes) : INodeStore
    {
        public IReadOnlyList<NodeRecord> Nodes { get; } = nodes;

        public Task<IReadOnlyList<NodeRecord>> GetAllAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NodeRecord>>(Nodes.Take(limit).ToArray());

        public Task<NodeRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Nodes.FirstOrDefault(node => node.Id == id));

        public Task<NodeRecord?> GetByPublicKeyAsync(
            ReadOnlyMemory<byte> publicKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Nodes.FirstOrDefault(node => node.PublicKey.AsSpan().SequenceEqual(publicKey.Span)));

        public Task<NodeRecord> FindOrCreateAsync(
            ReadOnlyMemory<byte> publicKey,
            string? name,
            DateTimeOffset seenUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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

    private sealed class TestProfileManager(IReadOnlyList<ConnectionProfile> profiles) : IConnectionProfileManager
    {
        public Task<IReadOnlyList<ConnectionProfile>> GetProfilesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(profiles);

        public Task<ConnectionProfile?> GetSelectedProfileAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ConnectionProfile?>(profiles.FirstOrDefault());

        public Task<ConnectionProfile> SelectAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(profiles.Single(profile => profile.Id == profileId));

        public Task<ConnectionProfile> SaveAndSelectAsync(
            ConnectionProfileDraft draft,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class EmptySerialPortCatalog : ISerialPortCatalog
    {
        public Task<IReadOnlyList<string>> GetPortNamesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
