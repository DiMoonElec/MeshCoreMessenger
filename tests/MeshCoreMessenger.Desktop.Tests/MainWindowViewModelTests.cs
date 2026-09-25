using Microsoft.Extensions.Logging.Abstractions;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
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

    private static MainWindowViewModel CreateViewModel(ILocalHistoryReader history)
    {
        var profiles = new ConnectionProfilesViewModel(
            new EmptyProfileManager(),
            new EmptySerialPortCatalog(),
            NullLogger<ConnectionProfilesViewModel>.Instance);
        return new MainWindowViewModel(history, profiles, NullLogger<MainWindowViewModel>.Instance);
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
        public IReadOnlyList<ConversationSummary> Conversations { get; init; } = [];
        public Dictionary<Guid, IReadOnlyList<HistoryMessage>> Messages { get; } = [];
        public List<Guid> RequestedConversations { get; } = [];
        public Exception? ConversationError { get; init; }
        public bool WaitForCancellation { get; init; }

        public async Task<IReadOnlyList<ConversationSummary>> GetConversationsAsync(
            int limit,
            CancellationToken cancellationToken = default)
        {
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (ConversationError is not null)
            {
                throw ConversationError;
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
