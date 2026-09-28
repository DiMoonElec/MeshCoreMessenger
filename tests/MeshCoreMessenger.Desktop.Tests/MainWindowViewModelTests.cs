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
    [Fact]
    public async Task EmptyHistoryProducesOfflineEmptyState()
    {
        var history = new FakeHistoryReader();
        var viewModel = CreateViewModel(history);

        await viewModel.LoadAsync(CancellationToken);

        Assert.Empty(viewModel.Conversations);
        Assert.Empty(viewModel.Messages);
        Assert.Null(viewModel.SelectedConversation);
        Assert.Equal("Локальная история пуста", viewModel.Status);
        Assert.Equal("Не подключено", viewModel.ConnectionStatus);
        Assert.False(viewModel.HasError);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task LoadsInitialConversationAndCanSelectAnother()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var history = new FakeHistoryReader
        {
            Conversations =
            [
                CreateSummary(firstId, "First", "first preview", 2),
                CreateSummary(secondId, "Second", "second preview", 1),
            ],
            Messages =
            {
                [firstId] = [CreateMessage(firstId, 2, "first body")],
                [secondId] = [CreateMessage(secondId, 1, "second body")],
            },
        };
        var viewModel = CreateViewModel(history);

        await viewModel.LoadAsync(CancellationToken);

        Assert.Equal(2, viewModel.Conversations.Count);
        Assert.Equal(firstId, viewModel.SelectedConversation?.Id);
        Assert.Equal("first body", Assert.Single(viewModel.Messages).Body);

        await viewModel.SelectConversationAsync(viewModel.Conversations[1], CancellationToken);

        Assert.Equal(secondId, viewModel.SelectedConversation?.Id);
        Assert.Equal("second body", Assert.Single(viewModel.Messages).Body);
        Assert.Equal([firstId, secondId], history.RequestedConversations);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task ReadFailureBecomesVisibleShellState()
    {
        var history = new FakeHistoryReader
        {
            ConversationError = new InvalidOperationException("test failure"),
        };
        var viewModel = CreateViewModel(history);

        await viewModel.LoadAsync(CancellationToken);

        Assert.True(viewModel.HasError);
        Assert.Equal("Ошибка локального хранилища", viewModel.Status);
        Assert.Contains("Не удалось", viewModel.ErrorMessage, StringComparison.Ordinal);
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
    public async Task MapsEverySupervisorState(
        ConnectionSupervisorState state,
        string expectedStatus)
    {
        var supervisor = new FakeConnectionSupervisor();
        var viewModel = CreateViewModel(new FakeHistoryReader(), supervisor: supervisor);
        var retryAt = DateTimeOffset.UtcNow.AddSeconds(5);

        supervisor.Publish(CreateSnapshot(state, "test reason", retryAt));

        Assert.Equal(expectedStatus, viewModel.ConnectionStatus);
        Assert.Contains("test reason", viewModel.ConnectionStatusDetail, StringComparison.Ordinal);
        if (state == ConnectionSupervisorState.RetryWaiting)
        {
            Assert.Contains("Следующая попытка", viewModel.ConnectionStatusDetail, StringComparison.Ordinal);
        }

        await viewModel.StopAsync();
    }

    [Fact]
    public async Task SupervisorStateIsAppliedThroughUiDispatcher()
    {
        var supervisor = new FakeConnectionSupervisor();
        var dispatcher = new QueuedUiDispatcher();
        var viewModel = CreateViewModel(
            new FakeHistoryReader(),
            supervisor: supervisor,
            dispatcher: dispatcher);

        supervisor.Publish(CreateSnapshot(ConnectionSupervisorState.Online));

        Assert.Equal("Не подключено", viewModel.ConnectionStatus);
        Assert.Equal(1, dispatcher.PendingCount);

        dispatcher.RunNext();

        Assert.Equal("Подключено", viewModel.ConnectionStatus);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task ConnectAndDisconnectCommandsDelegateToSupervisor()
    {
        var supervisor = new FakeConnectionSupervisor();
        var viewModel = CreateViewModel(new FakeHistoryReader(), supervisor: supervisor);

        await viewModel.ConnectCommand.ExecuteAsync(null);
        await viewModel.DisconnectCommand.ExecuteAsync(null);

        Assert.Equal(1, supervisor.ConnectCalls);
        Assert.Equal(1, supervisor.DisconnectCalls);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task CommittedMessageRereadsHistoryBeforeUpdatingProjection()
    {
        var conversationId = Guid.NewGuid();
        var history = new FakeHistoryReader();
        var notifications = new FakeMessageCommitNotifications();
        var dispatcher = new ImmediateUiDispatcher();
        var viewModel = CreateViewModel(
            history,
            notifications: notifications,
            dispatcher: dispatcher);
        await viewModel.LoadAsync(CancellationToken);
        var readsBeforeCommit = history.ConversationReads;

        history.Conversations = [CreateSummary(conversationId, "New", "committed preview", 1)];
        history.Messages[conversationId] = [CreateMessage(conversationId, 1, "committed body")];

        Assert.Empty(viewModel.Conversations);
        notifications.Publish(new StoredIncomingMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            conversationId,
            1,
            Inserted: true));

        await WaitUntilAsync(() => viewModel.Messages.Count == 1);
        Assert.True(history.ConversationReads > readsBeforeCommit);
        Assert.Equal(conversationId, Assert.Single(viewModel.Conversations).Id);
        Assert.Equal("committed body", Assert.Single(viewModel.Messages).Body);
        Assert.True(dispatcher.Calls >= 2);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task DuplicateCommitDoesNotReloadProjection()
    {
        var history = new FakeHistoryReader();
        var notifications = new FakeMessageCommitNotifications();
        var viewModel = CreateViewModel(history, notifications: notifications);
        await viewModel.LoadAsync(CancellationToken);
        var reads = history.ConversationReads;

        notifications.Publish(new StoredIncomingMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            Inserted: false));
        await Task.Delay(25, CancellationToken);

        Assert.Equal(reads, history.ConversationReads);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task CommitsDuringRefreshAreCoalescedIntoOneAdditionalRead()
    {
        var history = new FakeHistoryReader();
        var notifications = new FakeMessageCommitNotifications();
        var viewModel = CreateViewModel(history, notifications: notifications);
        await viewModel.LoadAsync(CancellationToken);
        var reads = history.ConversationReads;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        history.ConversationGate = gate;

        notifications.Publish(CreateCommit(inserted: true));
        await WaitUntilAsync(() => history.ConversationReads == reads + 1);
        notifications.Publish(CreateCommit(inserted: true));
        notifications.Publish(CreateCommit(inserted: true));
        gate.SetResult();

        await WaitUntilAsync(() => history.ConversationReads == reads + 2);
        await Task.Delay(25, CancellationToken);
        Assert.Equal(reads + 2, history.ConversationReads);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task LateCallbacksAfterStopDoNotChangeProjectionOrConnectionState()
    {
        var history = new FakeHistoryReader();
        var supervisor = new FakeConnectionSupervisor();
        var notifications = new FakeMessageCommitNotifications();
        var dispatcher = new ImmediateUiDispatcher();
        var viewModel = CreateViewModel(
            history,
            supervisor,
            notifications,
            dispatcher);
        await viewModel.LoadAsync(CancellationToken);
        var reads = history.ConversationReads;
        var dispatches = dispatcher.Calls;

        await viewModel.StopAsync();
        supervisor.Publish(CreateSnapshot(ConnectionSupervisorState.Online));
        notifications.Publish(new StoredIncomingMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            Inserted: true));

        Assert.Equal("Не подключено", viewModel.ConnectionStatus);
        Assert.Equal(reads, history.ConversationReads);
        Assert.Equal(dispatches, dispatcher.Calls);
    }

    [Fact]
    public async Task ShutdownPersistenceFailureRemainsVisibleAfterUiScenariosStop()
    {
        var viewModel = CreateViewModel(new FakeHistoryReader());
        await viewModel.StopAsync();

        await ((IDesktopUiLifetime)viewModel).ReportShutdownFailureAsync(
            new IOException("disk full"));

        Assert.True(viewModel.HasError);
        Assert.Contains("повторите закрытие", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Закрытие отменено", viewModel.Status, StringComparison.Ordinal);
    }

    private static MainWindowViewModel CreateViewModel(
        ILocalHistoryReader history,
        FakeConnectionSupervisor? supervisor = null,
        FakeMessageCommitNotifications? notifications = null,
        IUiDispatcher? dispatcher = null)
    {
        supervisor ??= new FakeConnectionSupervisor();
        var profiles = new ConnectionProfilesViewModel(
            new EmptyProfileManager(),
            supervisor,
            new EmptySerialPortCatalog(),
            NullLogger<ConnectionProfilesViewModel>.Instance);
        return new MainWindowViewModel(
            history,
            profiles,
            supervisor,
            notifications ?? new FakeMessageCommitNotifications(),
            dispatcher ?? new ImmediateUiDispatcher(),
            NullLogger<MainWindowViewModel>.Instance);
    }

    private static ConnectionSupervisorSnapshot CreateSnapshot(
        ConnectionSupervisorState state,
        string? reason = null,
        DateTimeOffset? nextAttemptUtc = null) =>
        new(state, 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), reason, nextAttemptUtc);

    private static StoredIncomingMessage CreateCommit(bool inserted) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, inserted);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static ConversationSummary CreateSummary(Guid id, string title, string preview, long sequence) =>
        new(
            id,
            Guid.NewGuid(),
            ConversationKind.UnknownContact,
            title,
            false,
            DateTimeOffset.UtcNow,
            sequence,
            MessageDirection.Incoming,
            StoredMessageKind.Text,
            preview,
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

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class FakeHistoryReader : ILocalHistoryReader
    {
        public IReadOnlyList<ConversationSummary> Conversations { get; set; } = [];
        public Dictionary<Guid, IReadOnlyList<HistoryMessage>> Messages { get; } = [];
        public List<Guid> RequestedConversations { get; } = [];
        public Exception? ConversationError { get; init; }
        public bool WaitForCancellation { get; init; }
        public int ConversationReads { get; private set; }
        public TaskCompletionSource? ConversationGate { get; set; }

        public async Task<IReadOnlyList<ConversationSummary>> GetConversationsAsync(
            int limit,
            CancellationToken cancellationToken = default)
        {
            ConversationReads++;
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (ConversationError is not null)
            {
                throw ConversationError;
            }

            if (ConversationGate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            return Conversations.Take(limit).ToArray();
        }

        public Task<IReadOnlyList<HistoryMessage>> GetMessagesAsync(
            Guid conversationId,
            long? beforeLocalSequence,
            int limit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedConversations.Add(conversationId);
            IReadOnlyList<HistoryMessage> result = Messages.TryGetValue(conversationId, out var messages)
                ? messages.Take(limit).ToArray()
                : [];
            return Task.FromResult(result);
        }
    }

    private sealed class EmptyProfileManager : IConnectionProfileManager
    {
        public Task<IReadOnlyList<ConnectionProfile>> GetProfilesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>([]);

        public Task<ConnectionProfile?> GetSelectedProfileAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ConnectionProfile?>(null);

        public Task<ConnectionProfile> SelectAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ConnectionProfile> SaveAndSelectAsync(
            ConnectionProfileDraft draft,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ConnectionProfile> UpdateExpectedNodePublicKeyAsync(
            Guid profileId,
            ReadOnlyMemory<byte> publicKey,
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
