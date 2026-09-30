using Microsoft.Extensions.Logging.Abstractions;
using Avalonia.Controls;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Controls;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class HistoryWindowViewModelTests
{
    private static readonly Guid NodeA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid NodeB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task LongScrollKeepsFivePageBudgetAndReloadsTrimmedRange()
    {
        var conversation = Guid.NewGuid();
        var reader = new FakeHistoryReader();
        reader.Seed(NodeA, conversation, 1_000);
        var model = Create(reader);
        await model.OpenAsync(NodeA, conversation, CancellationToken, dispatchResult: false);

        Assert.Equal((901, 1_000), Range(model));
        for (var index = 0; index < 5; index++)
        {
            await model.LoadOlderAsync(CancellationToken);
            Assert.InRange(model.Messages.Count, 1, HistoryWindowViewModel.MaximumMessages);
        }

        Assert.Equal(HistoryWindowViewModel.MaximumMessages, model.Messages.Count);
        Assert.Equal((401, 900), Range(model));
        Assert.True(model.CanLoadNewer);

        await model.LoadNewerAsync(CancellationToken);

        Assert.Equal(HistoryWindowViewModel.MaximumMessages, model.Messages.Count);
        Assert.Equal((501, 1_000), Range(model));
        Assert.Equal(model.Messages.Count, model.Messages.Select(item => item.Id).Distinct().Count());
        await model.StopAsync();
    }

    [Fact]
    public async Task PrependRequestsOriginalFirstMessageAsScrollAnchor()
    {
        var conversation = Guid.NewGuid();
        var reader = new FakeHistoryReader();
        reader.Seed(NodeA, conversation, 250);
        var model = Create(reader);
        await model.OpenAsync(NodeA, conversation, CancellationToken, dispatchResult: false);
        HistoryScrollRequestEventArgs? request = null;
        model.ScrollRequested += (_, args) => request = args;

        await model.LoadOlderAsync(CancellationToken);

        Assert.Equal(151, request?.AnchorSequence);
        Assert.False(request?.ScrollToEnd);
        Assert.Equal((51, 250), Range(model));
        await model.StopAsync();
    }

    [Fact]
    public async Task CommitsAutoAppendOnlyAtEndAndDuplicatesOrOtherNodesAreIgnored()
    {
        var conversation = Guid.NewGuid();
        var reader = new FakeHistoryReader();
        reader.Seed(NodeA, conversation, 3);
        var model = Create(reader);
        await model.OpenAsync(NodeA, conversation, CancellationToken, dispatchResult: false);
        model.ReportVisibleRange(1, 3, isWindowActive: true, isAtVisualEnd: true);
        Assert.Equal(1, model.FirstVisibleSequence);
        Assert.Equal(3, model.LastVisibleSequence);
        Assert.True(model.IsWindowActive);
        Assert.True(model.IsAtLatest);
        var scrollToEndCount = 0;
        model.ScrollRequested += (_, args) => scrollToEndCount += args.ScrollToEnd ? 1 : 0;

        var fourth = reader.Append(NodeA, conversation, 4);
        await model.HandleCommittedMessageAsync(fourth, CancellationToken);
        await model.HandleCommittedMessageAsync(fourth, CancellationToken);
        await model.HandleCommittedMessageAsync(
            reader.Append(NodeB, conversation, 5), CancellationToken);

        Assert.Equal([1, 2, 3, 4], model.Messages.Select(Number));
        Assert.Equal(1, scrollToEndCount);
        Assert.Equal(0, model.PendingNewMessageCount);

        model.ReportVisibleRange(1, 2, isWindowActive: true, isAtVisualEnd: false);
        var fifth = reader.Append(NodeA, conversation, 5);
        await model.HandleCommittedMessageAsync(fifth, CancellationToken);
        await model.HandleCommittedMessageAsync(fifth, CancellationToken);

        Assert.Equal([1, 2, 3, 4], model.Messages.Select(Number));
        Assert.Equal(1, model.PendingNewMessageCount);
        Assert.True(model.CanLoadNewer);

        await model.LoadNewerAsync(CancellationToken);
        Assert.Equal([1, 2, 3, 4, 5], model.Messages.Select(Number));
        Assert.Equal(1, model.PendingNewMessageCount);
        model.ReportVisibleRange(1, 5, isWindowActive: true, isAtVisualEnd: true);
        Assert.Equal(0, model.PendingNewMessageCount);

        model.ReportVisibleRange(1, 3, isWindowActive: true, isAtVisualEnd: false);
        var sixth = reader.Append(NodeA, conversation, 6);
        await model.HandleCommittedMessageAsync(sixth, CancellationToken);
        Assert.Equal(1, model.PendingNewMessageCount);

        await model.JumpToLatestAsync(CancellationToken);
        Assert.Equal([1, 2, 3, 4, 5, 6], model.Messages.Select(Number));
        Assert.Equal(0, model.PendingNewMessageCount);
        await model.StopAsync();
    }

    [Fact]
    public async Task LateConversationReadCannotReplaceNewContext()
    {
        var conversationA = Guid.NewGuid();
        var conversationB = Guid.NewGuid();
        var reader = new FakeHistoryReader();
        reader.Seed(NodeA, conversationA, 2);
        reader.Seed(NodeB, conversationB, 3);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.InitialGates[conversationA] = gate;
        var model = Create(reader);

        var slowA = model.OpenAsync(NodeA, conversationA, CancellationToken);
        await reader.WaitForInitialReadAsync(conversationA, CancellationToken);
        await model.OpenAsync(NodeB, conversationB, CancellationToken);
        gate.SetResult();
        await slowA;

        Assert.Equal([1, 2, 3], model.Messages.Select(Number));
        Assert.All(model.Messages, item => Assert.Equal(conversationB, item.ConversationId));
        await model.StopAsync();
    }

    [Fact]
    public void MessagePresentationPreservesUnicodeAndLabelsNonPlainContentHonestly()
    {
        var conversation = Guid.NewGuid();
        var unicode = new HistoryMessage(
            Guid.NewGuid(), 1, conversation, MessageDirection.Incoming,
            StoredMessageKind.Text, "Привет 👋 世界", DateTimeOffset.UtcNow)
        {
            TextType = 2,
        };
        var binary = new HistoryMessage(
            Guid.NewGuid(), 2, conversation, MessageDirection.Incoming,
            StoredMessageKind.Binary, null, DateTimeOffset.UtcNow)
        {
            BinaryDataType = 42,
        };

        var textItem = new HistoryMessageListItem(unicode);
        var binaryItem = new HistoryMessageListItem(binary);

        Assert.Equal("Привет 👋 世界", textItem.CopyText);
        Assert.Contains("room post", textItem.ContentLabel, StringComparison.Ordinal);
        Assert.Equal("Двоичное сообщение · тип 42", binaryItem.Body);
        Assert.Equal(binaryItem.Body, binaryItem.CopyText);
    }

    [Fact]
    public async Task StopCancelsAnOutstandingPageReadWithoutChangingWindow()
    {
        var conversation = Guid.NewGuid();
        var reader = new FakeHistoryReader();
        reader.Seed(NodeA, conversation, 250);
        var model = Create(reader);
        await model.OpenAsync(NodeA, conversation, CancellationToken, dispatchResult: false);
        var initial = model.Messages.Select(item => item.Id).ToArray();
        reader.RelativeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var load = model.LoadOlderAsync(CancellationToken);
        await reader.RelativeReadStarted.Task.WaitAsync(CancellationToken);
        await model.StopAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.Equal(initial, model.Messages.Select(item => item.Id));
    }

    [Fact]
    public async Task CollectionChangesFromAsyncReadsAreDispatched()
    {
        var conversation = Guid.NewGuid();
        var reader = new FakeHistoryReader();
        reader.Seed(NodeA, conversation, 120);
        var dispatcher = new QueuedUiDispatcher();
        var model = Create(reader, dispatcher);

        var open = model.OpenAsync(NodeA, conversation, CancellationToken);
        await WaitUntilAsync(() => dispatcher.PendingCount == 1);
        Assert.Empty(model.Messages);
        dispatcher.RunNext();
        await open;
        Assert.Equal(100, model.Messages.Count);

        var older = model.LoadOlderAsync(CancellationToken);
        await WaitUntilAsync(() => dispatcher.PendingCount == 1);
        Assert.Equal(100, model.Messages.Count);
        dispatcher.RunNext();
        await older;
        Assert.Equal(120, model.Messages.Count);
        await model.StopAsync();
    }

    [Fact]
    public void HistoryControlUsesVirtualizingPanelWithBoundedCache()
    {
        var control = new VirtualizedHistoryListBox();

        Assert.Equal(typeof(ListBox), control.StyleKey);
        var panel = Assert.IsType<VirtualizingStackPanel>(control.CreateItemsPanelForTest());
        Assert.Equal(VirtualizedHistoryListBox.ItemCacheLength, panel.CacheLength);
    }

    private static HistoryWindowViewModel Create(
        FakeHistoryReader reader,
        IUiDispatcher? dispatcher = null) =>
        new(reader, dispatcher ?? new ImmediateUiDispatcher(), NullLogger.Instance);

    private static (int First, int Last) Range(HistoryWindowViewModel model) =>
        (Number(model.Messages[0]), Number(model.Messages[^1]));

    private static int Number(HistoryMessageListItem item) =>
        int.Parse(item.CopyText[8..], System.Globalization.CultureInfo.InvariantCulture);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class FakeHistoryReader : ILocalHistoryReader
    {
        private readonly Dictionary<Guid, (Guid NodeId, List<HistoryMessage> Messages)> _histories = [];
        private readonly HashSet<Guid> _initialReads = [];

        public Dictionary<Guid, TaskCompletionSource> InitialGates { get; } = [];
        public TaskCompletionSource RelativeReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? RelativeGate { get; set; }

        public void Seed(Guid nodeId, Guid conversationId, int count)
        {
            _histories[conversationId] = (
                nodeId,
                Enumerable.Range(1, count)
                    .Select(number => Message(conversationId, number))
                    .ToList());
        }

        public StoredIncomingMessage Append(Guid nodeId, Guid conversationId, int number)
        {
            if (!_histories.TryGetValue(conversationId, out var history))
            {
                history = (nodeId, []);
                _histories[conversationId] = history;
            }

            var message = Message(conversationId, number);
            if (history.NodeId == nodeId)
            {
                history.Messages.Add(message);
            }
            return new StoredIncomingMessage(
                message.Id, Guid.NewGuid(), nodeId, conversationId, message.LocalSequence, true);
        }

        public Task<IReadOnlyList<ConversationSummary>> GetConversationsAsync(
            Guid nodeId, int limit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<HistoryMessage>> GetMessagesAsync(
            Guid nodeId, Guid conversationId, long? beforeLocalSequence, int limit,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<HistoryMessagePosition?> GetMessagePositionAsync(
            Guid nodeId, Guid conversationId, Guid messageId,
            CancellationToken cancellationToken = default)
        {
            var message = History(nodeId, conversationId).FirstOrDefault(item => item.Id == messageId);
            return Task.FromResult(message is null ? null : Position(nodeId, conversationId, message));
        }

        public async Task<HistoryMessagePage> GetMessagesBeforeAsync(
            Guid nodeId, Guid conversationId, HistoryMessagePosition? before, int limit,
            CancellationToken cancellationToken = default)
        {
            if (before is null)
            {
                _initialReads.Add(conversationId);
                if (InitialGates.TryGetValue(conversationId, out var initialGate))
                {
                    await initialGate.Task.WaitAsync(cancellationToken);
                }
            }
            else if (RelativeGate is { } relativeGate)
            {
                RelativeReadStarted.TrySetResult();
                await relativeGate.Task.WaitAsync(cancellationToken);
            }

            var all = History(nodeId, conversationId);
            var items = all
                .Where(item => before is null || item.LocalSequence < before.LocalSequence)
                .OrderByDescending(item => item.LocalSequence)
                .Take(limit)
                .OrderBy(item => item.LocalSequence)
                .ToArray();
            return Page(nodeId, conversationId, all, items);
        }

        public Task<HistoryMessagePage> GetMessagesAfterAsync(
            Guid nodeId, Guid conversationId, HistoryMessagePosition? after, int limit,
            CancellationToken cancellationToken = default)
        {
            var all = History(nodeId, conversationId);
            var items = all
                .Where(item => after is null || item.LocalSequence > after.LocalSequence)
                .OrderBy(item => item.LocalSequence)
                .Take(limit)
                .ToArray();
            return Task.FromResult(Page(nodeId, conversationId, all, items));
        }

        public Task<HistoryMessagePage> GetMessagesAroundAsync(
            HistoryMessagePosition position, int beforeLimit, int afterLimit,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task WaitForInitialReadAsync(Guid conversationId, CancellationToken cancellationToken)
        {
            while (!_initialReads.Contains(conversationId))
            {
                await Task.Delay(10, cancellationToken);
            }
        }

        private IReadOnlyList<HistoryMessage> History(Guid nodeId, Guid conversationId)
        {
            var history = _histories[conversationId];
            Assert.Equal(nodeId, history.NodeId);
            return history.Messages;
        }

        private static HistoryMessagePage Page(
            Guid nodeId,
            Guid conversationId,
            IReadOnlyList<HistoryMessage> all,
            IReadOnlyList<HistoryMessage> items)
        {
            var first = items.FirstOrDefault();
            var last = items.LastOrDefault();
            return new(
                items,
                first is null ? null : Position(nodeId, conversationId, first),
                last is null ? null : Position(nodeId, conversationId, last),
                first is not null && all.Any(item => item.LocalSequence < first.LocalSequence),
                last is not null && all.Any(item => item.LocalSequence > last.LocalSequence));
        }

        private static HistoryMessagePosition Position(
            Guid nodeId,
            Guid conversationId,
            HistoryMessage message) =>
            new(nodeId, conversationId, message.Id, message.LocalSequence);

        private static HistoryMessage Message(Guid conversationId, int number) =>
            new(
                GuidFromNumber(number),
                number,
                conversationId,
                MessageDirection.Incoming,
                StoredMessageKind.Text,
                $"message {number}",
                DateTimeOffset.UnixEpoch.AddSeconds(number));

        private static Guid GuidFromNumber(int number)
        {
            var bytes = new byte[16];
            BitConverter.TryWriteBytes(bytes, number);
            bytes[15] = 0xC5;
            return new Guid(bytes);
        }
    }
}
