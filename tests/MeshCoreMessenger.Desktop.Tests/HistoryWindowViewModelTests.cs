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
        Assert.Equal(HistoryScrollIntent.PreserveViewport, request?.Intent);
        Assert.Equal((51, 250), Range(model));
        await model.StopAsync();
    }

    [Fact]
    public async Task PageMutationIsBracketedBeforeCollectionChangesAndUsesPreserveIntent()
    {
        var conversation = Guid.NewGuid();
        var reader = new FakeHistoryReader();
        reader.Seed(NodeA, conversation, 1_000);
        var model = Create(reader);
        await model.OpenAsync(NodeA, conversation, CancellationToken, dispatchResult: false);
        var changing = false;
        var prepares = 0;
        var mutations = 0;
        var intents = new List<HistoryScrollIntent>();
        model.ViewportChanging += (_, _) => { changing = true; prepares++; };
        model.Messages.CollectionChanged += (_, _) => { Assert.True(changing); mutations++; };
        model.ScrollRequested += (_, args) => { Assert.True(changing); changing = false; intents.Add(args.Intent); };
        for (var index = 0; index < 6; index++) await model.LoadOlderAsync(CancellationToken);
        await model.LoadNewerAsync(CancellationToken);
        Assert.Equal(500, model.Messages.Count);
        Assert.Equal(7, prepares);
        Assert.True(mutations > 0);
        Assert.All(intents, intent => Assert.Equal(HistoryScrollIntent.PreserveViewport, intent));
        await model.JumpToLatestAsync(CancellationToken);
        Assert.Equal(HistoryScrollIntent.ToEnd, intents[^1]);
        Assert.False(changing);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReopeningMaterializedConversationDispatchesSearchResetAndPreservesInlineStartup(bool dispatchResult)
    {
        var conversation = Guid.NewGuid();
        var reader = new FakeHistoryReader(); reader.Seed(NodeA, conversation, 1);
        var dispatcher = new SearchResetAuditDispatcher();
        var model = Create(reader, dispatcher);
        await model.OpenAsync(NodeA, null, CancellationToken, dispatchResult: false);
        Assert.Equal(0, dispatcher.Calls);
        var notifications = 0;
        model.LoadMoreSearchResultsCommand.CanExecuteChanged += (_, _) =>
        {
            Assert.Equal(dispatchResult, dispatcher.IsDispatching);
            notifications++;
        };
        await Task.Run(() => model.OpenAsync(NodeA, conversation, CancellationToken, dispatchResult), CancellationToken);
        Assert.True(notifications > 0);
        Assert.Single(model.Messages);
        Assert.Equal(dispatchResult ? 2 : 0, dispatcher.Calls);
        await model.StopAsync();
    }

    private sealed class SearchResetAuditDispatcher : IUiDispatcher
    {
        private readonly AsyncLocal<bool> _dispatching = new();
        public bool IsDispatching => _dispatching.Value;
        public int Calls { get; private set; }
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var previous = _dispatching.Value; _dispatching.Value = true;
            try { action(); }
            finally { _dispatching.Value = previous; }
            return Task.CompletedTask;
        }
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
        dispatcher.RunNext(); // Search reset/command notifications.
        await WaitUntilAsync(() => dispatcher.PendingCount == 1);
        Assert.Empty(model.Messages);
        dispatcher.RunNext(); // Loaded history publication.
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
    public async Task ReadWatermarkRequiresActiveWindowAndVisibleFirstUnreadBoundary()
    {
        var conversation = Guid.NewGuid();
        var reader = new FakeHistoryReader();
        reader.Seed(NodeA, conversation, 10);
        var readStates = new FakeConversationReadStateService();
        readStates.Set(new ConversationReadState(
            NodeA,
            conversation,
            2,
            8,
            Position(NodeA, conversation, reader.MessageAt(NodeA, conversation, 3))));
        var model = Create(reader, readStates: readStates);
        await model.OpenAsync(NodeA, conversation, CancellationToken, dispatchResult: false);

        model.ReportVisibleRange(3, 5, isWindowActive: false, isAtVisualEnd: false);
        model.ReportVisibleRange(4, 6, isWindowActive: true, isAtVisualEnd: false);
        Assert.Empty(readStates.Advances);
        Assert.Equal(8, model.UnreadCount);

        model.ReportVisibleRange(3, 5, isWindowActive: true, isAtVisualEnd: false);
        await WaitUntilAsync(() => readStates.Advances.Count == 1);

        Assert.Equal(5, Assert.Single(readStates.Advances).LocalSequence);
        await WaitUntilAsync(() => model.UnreadCount == 0);
        await model.StopAsync();
    }

    [Fact]
    public async Task OverlappingScrollRangesContinueWhilePreviousReadWriteIsPending()
    {
        var conversation = Guid.NewGuid();
        var reader = new FakeHistoryReader();
        reader.Seed(NodeA, conversation, 10);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readStates = new FakeConversationReadStateService { AdvanceGate = gate };
        readStates.Set(new ConversationReadState(
            NodeA,
            conversation,
            0,
            10,
            Position(NodeA, conversation, reader.MessageAt(NodeA, conversation, 1))));
        var model = Create(reader, readStates: readStates);
        await model.OpenAsync(NodeA, conversation, CancellationToken, dispatchResult: false);

        model.ReportVisibleRange(1, 3, isWindowActive: true, isAtVisualEnd: false);
        await WaitUntilAsync(() => readStates.Advances.Count == 1);
        model.ReportVisibleRange(3, 5, isWindowActive: true, isAtVisualEnd: false);
        await WaitUntilAsync(() => readStates.Advances.Count == 2);
        model.ReportVisibleRange(5, 7, isWindowActive: true, isAtVisualEnd: false);
        await WaitUntilAsync(() => readStates.Advances.Count == 3);

        Assert.Equal([3, 5, 7], readStates.Advances.Select(item => item.LocalSequence));
        model.ReportVisibleRange(9, 10, isWindowActive: true, isAtVisualEnd: true);
        Assert.Equal(3, readStates.Advances.Count);

        gate.SetResult();
        await model.StopAsync();
    }

    [Fact]
    public async Task OpeningAndLoadingPagesNeverMarksMessagesReadByItself()
    {
        var conversation = Guid.NewGuid();
        var reader = new FakeHistoryReader();
        reader.Seed(NodeA, conversation, 250);
        var readStates = new FakeConversationReadStateService();
        readStates.Set(new ConversationReadState(
            NodeA,
            conversation,
            0,
            250,
            Position(NodeA, conversation, reader.MessageAt(NodeA, conversation, 1))));
        var model = Create(reader, readStates: readStates);

        await model.OpenAsync(NodeA, conversation, CancellationToken, dispatchResult: false);
        await model.LoadOlderAsync(CancellationToken);
        await model.JumpToLatestAsync(CancellationToken);

        Assert.Empty(readStates.Advances);
        Assert.Equal(250, model.UnreadCount);
        await model.StopAsync();
    }

    [Fact]
    public async Task JumpToFirstUnreadLoadsAroundExactPositionAndRequestsItsAnchor()
    {
        var conversation = Guid.NewGuid();
        var reader = new FakeHistoryReader();
        reader.Seed(NodeA, conversation, 250);
        var readStates = new FakeConversationReadStateService();
        var firstUnread = Position(NodeA, conversation, reader.MessageAt(NodeA, conversation, 50));
        readStates.Set(new ConversationReadState(NodeA, conversation, 49, 201, firstUnread));
        var model = Create(reader, readStates: readStates);
        await model.OpenAsync(NodeA, conversation, CancellationToken, dispatchResult: false);
        HistoryScrollRequestEventArgs? request = null;
        model.ScrollRequested += (_, args) => request = args;

        await model.JumpToFirstUnreadAsync(CancellationToken);

        Assert.Equal(50, request?.AnchorSequence);
        Assert.False(request?.ScrollToEnd);
        Assert.Equal(HistoryScrollIntent.ToMessage, request?.Intent);
        Assert.Contains(model.Messages, item => item.LocalSequence == 50);
        Assert.InRange(model.Messages.Count, 1, HistoryWindowViewModel.PageSize);
        Assert.Empty(readStates.Advances);
        await model.StopAsync();
    }

    [Fact]
    public async Task FailedReadWriteKeepsUnreadStateAndSurfacesError()
    {
        var conversation = Guid.NewGuid();
        var reader = new FakeHistoryReader();
        reader.Seed(NodeA, conversation, 3);
        var readStates = new FakeConversationReadStateService
        {
            AdvanceFailure = new IOException("disk full"),
        };
        readStates.Set(new ConversationReadState(
            NodeA,
            conversation,
            0,
            3,
            Position(NodeA, conversation, reader.MessageAt(NodeA, conversation, 1))));
        var model = Create(reader, readStates: readStates);
        await model.OpenAsync(NodeA, conversation, CancellationToken, dispatchResult: false);

        model.ReportVisibleRange(1, 3, isWindowActive: true, isAtVisualEnd: true);
        await WaitUntilAsync(() => model.HasReadError);

        Assert.Equal(3, model.UnreadCount);
        Assert.Equal(1, model.FirstUnreadPosition?.LocalSequence);
        await model.StopAsync();
    }

    [Fact]
    public async Task StopWaitsForAcceptedReadWriteWithoutCancellingIt()
    {
        var conversation = Guid.NewGuid();
        var reader = new FakeHistoryReader();
        reader.Seed(NodeA, conversation, 2);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readStates = new FakeConversationReadStateService { AdvanceGate = gate };
        readStates.Set(new ConversationReadState(
            NodeA,
            conversation,
            0,
            2,
            Position(NodeA, conversation, reader.MessageAt(NodeA, conversation, 1))));
        var model = Create(reader, readStates: readStates);
        await model.OpenAsync(NodeA, conversation, CancellationToken, dispatchResult: false);
        model.ReportVisibleRange(1, 2, isWindowActive: true, isAtVisualEnd: true);
        await WaitUntilAsync(() => readStates.Advances.Count == 1);

        var stop = model.StopAsync();
        Assert.False(stop.IsCompleted);
        gate.SetResult();
        await stop;

        Assert.Equal(2, Assert.Single(readStates.Advances).LocalSequence);
    }

    [Fact]
    public async Task IncomingDuringReadCommitRemainsUnreadUntilItIsActuallyVisible()
    {
        var conversation = Guid.NewGuid();
        var reader = new FakeHistoryReader();
        reader.Seed(NodeA, conversation, 2);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readStates = new FakeConversationReadStateService { AdvanceGate = gate };
        readStates.Set(new ConversationReadState(
            NodeA,
            conversation,
            0,
            2,
            Position(NodeA, conversation, reader.MessageAt(NodeA, conversation, 1))));
        readStates.AdvanceResult = through => through.LocalSequence == 2
            ? new ConversationReadState(
                NodeA,
                conversation,
                2,
                1,
                Position(NodeA, conversation, reader.MessageAt(NodeA, conversation, 3)))
            : new ConversationReadState(NodeA, conversation, 3, 0, null);
        var model = Create(reader, readStates: readStates);
        await model.OpenAsync(NodeA, conversation, CancellationToken, dispatchResult: false);
        model.ReportVisibleRange(1, 2, isWindowActive: true, isAtVisualEnd: true);
        await WaitUntilAsync(() => readStates.Advances.Count == 1);

        var thirdCommit = reader.Append(NodeA, conversation, 3);
        readStates.Set(new ConversationReadState(
            NodeA,
            conversation,
            0,
            3,
            Position(NodeA, conversation, reader.MessageAt(NodeA, conversation, 1))));
        await model.HandleCommittedMessageAsync(thirdCommit, CancellationToken);
        gate.SetResult();
        await WaitUntilAsync(() => model.UnreadCount == 1);

        Assert.Equal([2], readStates.Advances.Select(item => item.LocalSequence));
        model.ReportVisibleRange(1, 3, isWindowActive: true, isAtVisualEnd: true);
        await WaitUntilAsync(() => readStates.Advances.Count == 2 && model.UnreadCount == 0);
        Assert.Equal([2, 3], readStates.Advances.Select(item => item.LocalSequence));
        await model.StopAsync();
    }

    [Fact]
    public async Task LateReadCompletionCannotChangeNewConversationState()
    {
        var conversationA = Guid.NewGuid();
        var conversationB = Guid.NewGuid();
        var reader = new FakeHistoryReader();
        reader.Seed(NodeA, conversationA, 2);
        reader.Seed(NodeB, conversationB, 2);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readStates = new FakeConversationReadStateService { AdvanceGate = gate };
        readStates.Set(new ConversationReadState(
            NodeA,
            conversationA,
            0,
            2,
            Position(NodeA, conversationA, reader.MessageAt(NodeA, conversationA, 1))));
        readStates.Set(new ConversationReadState(
            NodeB,
            conversationB,
            0,
            2,
            Position(NodeB, conversationB, reader.MessageAt(NodeB, conversationB, 1))));
        var model = Create(reader, readStates: readStates);
        await model.OpenAsync(NodeA, conversationA, CancellationToken, dispatchResult: false);
        model.ReportVisibleRange(1, 2, isWindowActive: true, isAtVisualEnd: true);
        await WaitUntilAsync(() => readStates.Advances.Count == 1);

        await model.OpenAsync(NodeB, conversationB, CancellationToken, dispatchResult: false);
        gate.SetResult();
        await Task.Delay(20, CancellationToken);

        Assert.Equal(2, model.UnreadCount);
        Assert.Equal(conversationB, model.FirstUnreadPosition?.ConversationId);
        Assert.All(model.Messages, item => Assert.Equal(conversationB, item.ConversationId));
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

    [Fact]
    public async Task SearchIsPagedAndJumpLoadsAnOutsideResultWithoutMarkingItRead()
    {
        var conversation = Guid.NewGuid();
        var reader = new FakeHistoryReader();
        reader.Seed(NodeA, conversation, 300);
        var readStates = new FakeConversationReadStateService();
        var model = Create(reader, readStates: readStates);
        await model.OpenAsync(NodeA, conversation, CancellationToken, dispatchResult: false);
        HistoryScrollRequestEventArgs? request = null;
        model.ScrollRequested += (_, args) => request = args;

        model.SearchText = "message 1";
        await WaitUntilAsync(() => !model.IsSearching && model.SearchResults.Count > 0);
        Assert.InRange(model.SearchResults.Count, 1, HistoryWindowViewModel.SearchPageSize);
        Assert.True(model.CanLoadMoreSearchResults);
        var result = model.SearchResults.First(item => item.Position.LocalSequence < 201);

        await model.JumpToSearchResultAsync(result, CancellationToken);

        Assert.Contains(model.Messages, item => item.LocalSequence == result.Position.LocalSequence);
        Assert.True(model.Messages.Single(item => item.LocalSequence == result.Position.LocalSequence).IsSearchMatch);
        Assert.Equal(result.Position.LocalSequence, request?.AnchorSequence);
        Assert.Equal(HistoryScrollIntent.ToMessage, request?.Intent);
        Assert.Empty(readStates.Advances);
        await model.StopAsync();
    }

    [Fact]
    public async Task MissingSearchTargetSurfacesAnErrorAndRapidOldResultCannotReplaceNewQuery()
    {
        var conversation = Guid.NewGuid();
        var reader = new FakeHistoryReader();
        reader.Seed(NodeA, conversation, 50);
        var dispatcher = new QueuedUiDispatcher();
        var model = Create(reader, dispatcher);
        await model.OpenAsync(NodeA, conversation, CancellationToken, dispatchResult: false);

        model.SearchText = "message 1";
        await WaitUntilAsync(() => dispatcher.PendingCount == 1);
        model.SearchText = "message 4";
        await WaitUntilAsync(() => dispatcher.PendingCount == 2);
        dispatcher.RunNext();
        dispatcher.RunNext();
        await WaitUntilAsync(() => !model.IsSearching);
        Assert.NotEmpty(model.SearchResults);
        Assert.All(model.SearchResults, item => Assert.Contains("message 4", item.Preview, StringComparison.Ordinal));

        var missing = model.SearchResults[0];
        reader.Remove(NodeA, conversation, missing.Position.MessageId);
        var jump = model.JumpToSearchResultAsync(missing, CancellationToken);
        await WaitUntilAsync(() => dispatcher.PendingCount == 1);
        dispatcher.RunNext();
        await jump;
        Assert.Contains("больше не существует", model.SearchStatus, StringComparison.Ordinal);
        await model.StopAsync();
    }

    [Fact]
    public async Task DebounceIsInjectableAndRapidQueryCancelsWithoutBlockingTheCaller()
    {
        var conversation = Guid.NewGuid();
        var reader = new FakeHistoryReader();
        reader.Seed(NodeA, conversation, 50);
        var delay = new ControlledSearchDelay();
        var model = Create(reader, searchDelay: delay);
        await model.OpenAsync(NodeA, conversation, CancellationToken, dispatchResult: false);

        model.SearchText = "message 1";
        Assert.True(model.IsSearching);
        Assert.Single(delay.Requests);
        Assert.Equal(TimeSpan.FromMilliseconds(250), delay.Requests[0].Delay);

        model.SearchText = "message 4";
        Assert.Equal(2, delay.Requests.Count);
        Assert.True(delay.Requests[0].CancellationToken.IsCancellationRequested);
        Assert.Empty(model.SearchResults);

        delay.Requests[1].Completion.SetResult();
        await WaitUntilAsync(() => !model.IsSearching);
        Assert.All(model.SearchResults, item => Assert.Contains("message 4", item.Preview, StringComparison.Ordinal));
        await model.StopAsync();
    }

    [Fact]
    public async Task HistoryClearRejectsLatePageAndOldIncomingCommitAfterReopening()
    {
        var conversation = Guid.NewGuid();
        var reader = new FakeHistoryReader();
        reader.Seed(NodeA, conversation, 250);
        var model = Create(reader);
        await model.OpenAsync(NodeA, conversation, CancellationToken, dispatchResult: false);
        var oldMessage = reader.MessageAt(NodeA, conversation, 250);
        reader.CaptureRelativePageBeforeWait = true;
        reader.RelativeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var loading = model.LoadOlderAsync(CancellationToken);
        await reader.RelativeReadStarted.Task.WaitAsync(CancellationToken);
        model.InvalidateHistoryClear(new(NodeA, conversation, 250, 250));
        reader.Seed(NodeA, conversation, 0);
        await model.OpenAsync(NodeA, conversation, CancellationToken, dispatchResult: false);
        reader.RelativeGate.SetResult(); await loading;
        Assert.Empty(model.Messages);
        model.ReportVisibleRange(null, null, true, false);
        await model.HandleCommittedMessageAsync(new(oldMessage.Id, Guid.NewGuid(), NodeA, conversation, 250, true), CancellationToken);
        Assert.Equal(0, model.PendingNewMessageCount);
        Assert.Empty(model.Messages);
        await model.StopAsync();
    }

    private static HistoryWindowViewModel Create(
        FakeHistoryReader reader,
        IUiDispatcher? dispatcher = null,
        FakeConversationReadStateService? readStates = null,
        ISearchDelay? searchDelay = null)
    {
        readStates ??= new FakeConversationReadStateService();
        return new(
            reader,
            readStates,
            readStates,
            dispatcher ?? new ImmediateUiDispatcher(),
            searchDelay ?? new ImmediateSearchDelay(),
            NullLogger.Instance);
    }

    private static (int First, int Last) Range(HistoryWindowViewModel model) =>
        (Number(model.Messages[0]), Number(model.Messages[^1]));

    private static int Number(HistoryMessageListItem item) =>
        int.Parse(item.CopyText[8..], System.Globalization.CultureInfo.InvariantCulture);

    private static HistoryMessagePosition Position(
        Guid nodeId,
        Guid conversationId,
        HistoryMessage message) =>
        new(nodeId, conversationId, message.Id, message.LocalSequence);

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
        public bool CaptureRelativePageBeforeWait { get; set; }

        public void Seed(Guid nodeId, Guid conversationId, int count)
        {
            _histories[conversationId] = (
                nodeId,
                Enumerable.Range(1, count)
                    .Select(number => Message(conversationId, number))
                    .ToList());
        }

        public HistoryMessage MessageAt(Guid nodeId, Guid conversationId, int number) =>
            History(nodeId, conversationId).Single(item =>
                int.Parse(item.Text![8..], System.Globalization.CultureInfo.InvariantCulture) == number);

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

        public void Remove(Guid nodeId, Guid conversationId, Guid messageId)
        {
            var history = _histories[conversationId];
            Assert.Equal(nodeId, history.NodeId);
            history.Messages.RemoveAll(item => item.Id == messageId);
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
            else if (!CaptureRelativePageBeforeWait && RelativeGate is { } relativeGate)
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
            var page = Page(nodeId, conversationId, all, items);
            if (before is not null && CaptureRelativePageBeforeWait && RelativeGate is { } capturedGate)
            {
                RelativeReadStarted.TrySetResult();
                await capturedGate.Task.WaitAsync(cancellationToken);
            }
            return page;
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
            CancellationToken cancellationToken = default)
        {
            var all = History(position.NodeId, position.ConversationId);
            var anchorIndex = all.ToList().FindIndex(item =>
                item.Id == position.MessageId && item.LocalSequence == position.LocalSequence);
            if (anchorIndex < 0)
            {
                throw new KeyNotFoundException();
            }

            var first = Math.Max(0, anchorIndex - beforeLimit);
            var lastExclusive = Math.Min(all.Count, anchorIndex + afterLimit + 1);
            var items = all.Skip(first).Take(lastExclusive - first).ToArray();
            return Task.FromResult(new HistoryMessagePage(
                items,
                Position(position.NodeId, position.ConversationId, items[0]),
                Position(position.NodeId, position.ConversationId, items[^1]),
                first > 0,
                lastExclusive < all.Count));
        }

        public Task<HistoryMessageSearchPage> SearchMessagesAsync(
            Guid nodeId,
            Guid conversationId,
            string query,
            HistoryMessagePosition? before,
            int limit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var all = History(nodeId, conversationId);
            var matches = all
                .Where(item => item.MessageKind == StoredMessageKind.Text &&
                    item.Text?.Contains(query, StringComparison.Ordinal) == true &&
                    (before is null || item.LocalSequence < before.LocalSequence))
                .OrderByDescending(item => item.LocalSequence)
                .Take(limit + 1)
                .ToArray();
            var items = matches.Take(limit)
                .Select(item => new HistoryMessageSearchResult(
                    Position(nodeId, conversationId, item), item.Direction, item.Text!, item.ReceivedUtc))
                .ToArray();
            var next = matches.Length > limit ? items[^1].Position : null;
            return Task.FromResult(new HistoryMessageSearchPage(items, next));
        }

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
