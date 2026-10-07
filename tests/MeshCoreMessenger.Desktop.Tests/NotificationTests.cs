using System.Collections.Concurrent;
using System.Threading.Channels;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Notifications;
using MeshCoreMessenger.Desktop.Preferences;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class NotificationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly Guid Node = Guid.NewGuid();

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FourSettingsCombinationsIncludeUnknownSendersAndChannels(bool personal, bool channels)
    {
        var preferences = new DesktopPreferences(new Settings());
        preferences.SetNotifyPrivateMessages(personal); preferences.SetNotifyChannelMessages(channels);
        var reader = new Reader(); var policy = Policy(preferences, reader);
        foreach (var kind in Enum.GetValues<ConversationKind>())
        {
            var category = kind is ConversationKind.Contact or ConversationKind.UnknownContact ? IncomingMessageCategory.Private : IncomingMessageCategory.Channel;
            var group = Group(category);
            reader.Value = Details(group, kind);
            var prepared = await policy.PrepareAsync(new MessageNotificationRequest([group], false), Token);
            Assert.Equal(category == IncomingMessageCategory.Private ? personal : channels, prepared is not null);
            if (prepared is not null)
            {
                Assert.IsType<MessageNotificationTarget>(prepared.Target);
                Assert.Equal("message", prepared.Body);
                if (kind is ConversationKind.UnknownContact or ConversationKind.UnknownChannel)
                    Assert.DoesNotContain("secret", prepared.Title);
            }
        }
    }

    [Theory]
    [InlineData(StoredMessageKind.Binary, 0, "Двоичное сообщение")]
    [InlineData(StoredMessageKind.Text, 1, "Новое сообщение")]
    [InlineData(StoredMessageKind.Text, 255, "Новое сообщение")]
    [InlineData(StoredMessageKind.Text, 2, "message")]
    public async Task BinaryAndUnknownSubtypesHaveSafePreview(StoredMessageKind kind, int subtype, string expected)
    {
        var group = Group(); var reader = new Reader { Value = Details(group) with { MessageKind = kind, TextType = subtype } };
        var request = await Policy(new(new Settings()), reader).PrepareAsync(new MessageNotificationRequest([group], false), Token);
        Assert.Equal(expected, request!.Body);
    }

    [Fact]
    public void PreviewRemovesControlAndBidiCharactersWithoutSplittingEmoji()
    {
        Assert.Equal("one two", MessageNotificationPolicy.SafeText("one\n\u202Etwo\0", 160, "fallback"));
        Assert.Equal("🐈‍⬛…", MessageNotificationPolicy.SafeText("🐈‍⬛ second", 1, "fallback"));
        Assert.Equal("fallback", MessageNotificationPolicy.SafeText("\u202E", 10, "fallback"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task OnlyActualVisibleMessageSuppressesBanner(bool visible, bool overflow)
    {
        var group = Group() with { IsOverflow = overflow }; var reader = new Reader { Value = Details(group) };
        var view = new Visibility { Visible = visible };
        var request = await Policy(new(new Settings()), reader, view).PrepareAsync(new MessageNotificationRequest([group], overflow), Token);
        Assert.Equal(!visible || overflow, request is not null);
    }

    [Fact]
    public async Task DeletedOutgoingMismatchedAndReadFailuresDoNotNotify()
    {
        var group = Group(); var reader = new Reader(); var policy = Policy(new(new Settings()), reader);
        var request = new MessageNotificationRequest([group], false);
        Assert.Null(await policy.PrepareAsync(request, Token));
        reader.Value = Details(group) with { Direction = MessageDirection.Outgoing };
        Assert.Null(await policy.PrepareAsync(request, Token));
        reader.Value = Details(group, ConversationKind.Channel);
        Assert.Null(await policy.PrepareAsync(request, Token));
        reader.Throw = true;
        Assert.Null(await policy.PrepareAsync(request, Token));
    }

    [Fact]
    public async Task SettingsAreRecheckedAfterSlowReadAndGenericRequestsBypassMessageSettings()
    {
        var settings = new DesktopPreferences(new Settings()); var group = Group();
        var reader = new Reader { Value = Details(group), Gate = NewGate() };
        var policy = Policy(settings, reader);
        var prepare = policy.PrepareAsync(new MessageNotificationRequest([group], false), Token);
        await reader.Started.Task.WaitAsync(Token);
        settings.SetNotifyPrivateMessages(false);
        reader.Gate.SetResult();
        Assert.Null(await prepare);
        var generic = new NotificationRequest("Operation", "Finished", new OpenApplicationTarget(), "operation");
        Assert.Same(generic, await policy.PrepareAsync(generic, Token));
    }

    [Fact]
    public async Task BurstIsOneRequestAndDuplicateCommitIsIgnored()
    {
        var source = new Commits(); var sink = new Sink(); var clock = new Clock();
        await using var coordinator = new MessageNotificationCoordinator(source, sink, clock);
        coordinator.Start(); coordinator.Start();
        var group = Group();
        source.Publish(group.Latest);
        source.Publish(group.Latest with { Message = group.Latest.Message with { Inserted = false } });
        source.Publish(group.Latest with { Message = group.Latest.Message with { MessageId = Guid.NewGuid(), LocalSequence = 2 } });
        (await clock.NextAsync()).Fire();
        var request = Assert.IsType<MessageNotificationRequest>(await sink.NextAsync());
        Assert.Equal(2, Assert.Single(request.Groups).Count);
        await coordinator.StopAsync();
        Assert.Equal(0, source.Subscribers);
        source.Publish(Group().Latest);
        Assert.Empty(sink.Pending);
    }

    [Fact]
    public async Task InitialBatchWaitsForCompletionAndDoesNotUseCurrentConnectionState()
    {
        var source = new Commits(); var sink = new Sink(); var clock = new Clock();
        await using var coordinator = new MessageNotificationCoordinator(source, sink, clock);
        coordinator.Start();
        var batch = new IncomingSynchronization(Guid.NewGuid(), Node);
        var personal = Group().Latest with { SessionId = batch.SessionId, InitialSynchronization = batch };
        source.Publish(personal);
        source.Publish(Group(IncomingMessageCategory.Channel).Latest with { SessionId = batch.SessionId, InitialSynchronization = batch });
        (await clock.NextAsync()).Fire();
        // Completion wakes the coordinator; all initial groups become one request.
        Assert.Empty(sink.Pending);
        batch.Complete();
        (await clock.NextAsync()).Fire();
        var summary = Assert.IsType<MessageNotificationRequest>(await sink.NextAsync());
        Assert.True(summary.IsSummary);
        Assert.Equal(2, summary.Groups.Count);
        source.Publish(Group().Latest with { SessionId = Guid.NewGuid() });
        (await clock.NextAsync()).Fire();
        Assert.False(Assert.IsType<MessageNotificationRequest>(await sink.NextAsync()).IsSummary);
    }

    [Fact]
    public async Task AbortedInitialSessionDoesNotReplayButAnotherNodeIsNotFilteredByViewedNode()
    {
        var source = new Commits(); var sink = new Sink(); var clock = new Clock();
        await using var coordinator = new MessageNotificationCoordinator(source, sink, clock);
        coordinator.Start();
        var old = new IncomingSynchronization(Guid.NewGuid(), Node); old.Abort();
        source.Publish(Group().Latest with { InitialSynchronization = old });
        var other = Group().Latest with { Message = Group().Latest.Message with { NodeId = Guid.NewGuid() } };
        source.Publish(other);
        (await clock.NextAsync()).Fire();
        var ready = Assert.IsType<MessageNotificationRequest>(await sink.NextAsync());
        Assert.Equal(other.Message.NodeId, Assert.Single(ready.Groups).Latest.Message.NodeId);
        Assert.Empty(sink.Pending);
    }

    [Fact]
    public async Task MessageOverloadProducesBoundedRequestsAndSummary()
    {
        var source = new Commits(); var sink = new Sink(); var clock = new Clock();
        await using var coordinator = new MessageNotificationCoordinator(source, sink, clock);
        coordinator.Start();
        for (var index = 0; index < 1000; index++) source.Publish(Group().Latest);
        (await clock.NextAsync()).Fire();
        var request = Assert.IsType<MessageNotificationRequest>(await sink.NextAsync());
        Assert.True(request.IsSummary);
        Assert.Equal(MessageNotificationCoordinator.Capacity + 1, request.Groups.Count);
        Assert.Equal(1000, request.Groups.Sum(group => group.Count));
        Assert.Empty(sink.Pending);
    }

    [Fact]
    public async Task StopCancelsInitialSummaryAndRestartDoesNotReplayIt()
    {
        var source = new Commits(); var sink = new Sink(); var clock = new Clock();
        var batch = new IncomingSynchronization(Guid.NewGuid(), Node);
        await using (var first = new MessageNotificationCoordinator(source, sink, clock))
        {
            first.Start(); source.Publish(Group().Latest with { InitialSynchronization = batch });
            await clock.NextAsync();
            await first.StopAsync();
        }
        batch.Complete();
        Assert.Empty(sink.Pending);
        await using var restarted = new MessageNotificationCoordinator(source, sink, clock);
        restarted.Start(); source.Publish(Group().Latest);
        (await clock.NextAsync()).Fire();
        var request = Assert.IsType<MessageNotificationRequest>(await sink.NextAsync());
        Assert.False(request.IsSummary);
        Assert.Equal(1, Assert.Single(request.Groups).Count);
    }

    [Fact]
    public async Task MixedCategoryOverloadPreservesSeparateCheckboxCounts()
    {
        var source = new Commits(); var sink = new Sink(); var clock = new Clock();
        await using var coordinator = new MessageNotificationCoordinator(source, sink, clock);
        coordinator.Start();
        for (var index = 0; index < 1000; index++)
            source.Publish(Group(index % 2 == 0 ? IncomingMessageCategory.Private : IncomingMessageCategory.Channel).Latest);
        (await clock.NextAsync()).Fire();
        var request = Assert.IsType<MessageNotificationRequest>(await sink.NextAsync());
        Assert.True(request.IsSummary);
        Assert.Equal(500, request.Groups.Where(group => group.Latest.Category == IncomingMessageCategory.Private).Sum(group => group.Count));
        Assert.Equal(500, request.Groups.Where(group => group.Latest.Category == IncomingMessageCategory.Channel).Sum(group => group.Count));
        Assert.Empty(sink.Pending);
    }

    [Fact]
    public async Task GenericServiceReplacesQueuedKeyAndIsolatesThrowingAdapter()
    {
        var adapter = new Adapter { BlockFirst = NewGate(), ThrowFirst = true };
        await using var service = Service(adapter);
        service.Submit(new("first", "", CoalescingKey: "first"));
        await adapter.Started.Task.WaitAsync(Token);
        service.Submit(new("older", "", CoalescingKey: "replace"));
        service.Submit(new("newer", "", CoalescingKey: "replace"));
        adapter.BlockFirst.SetResult();
        Assert.Equal("newer", (await adapter.NextAsync()).Title);
        await service.StopAsync();
        Assert.False(service.Submit(new("late", "")));
    }

    [Fact]
    public async Task SlowAdapterDoesNotBlockSubmissionAndStopCancelsPendingWork()
    {
        var adapter = new Adapter { BlockFirst = NewGate() };
        await using var service = Service(adapter);
        service.Submit(new("first", "")); await adapter.Started.Task.WaitAsync(Token);
        for (var index = 0; index < 1000; index++) Assert.True(service.Submit(new("other event", index.ToString())));
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2), Token);
        Assert.Empty(adapter.Delivered);
    }

    [Fact]
    public async Task FinalPolicyRunsAfterQueuedRequestWaitsBehindSlowAdapter()
    {
        var adapter = new Adapter { BlockFirst = NewGate() };
        var settings = new DesktopPreferences(new Settings()); var group = Group();
        var policy = Policy(settings, new Reader { Value = Details(group) });
        await using var service = new DesktopNotificationService(adapter, [policy], NullLogger<DesktopNotificationService>.Instance);
        service.Submit(new("generic", "")); await adapter.Started.Task.WaitAsync(Token);
        service.Submit(new MessageNotificationRequest([group], false));
        settings.SetNotifyPrivateMessages(false);
        service.Submit(new("sentinel", ""));
        adapter.BlockFirst.SetResult();
        Assert.Equal("generic", (await adapter.NextAsync()).Title);
        Assert.Equal("sentinel", (await adapter.NextAsync()).Title);
        Assert.Empty(adapter.Delivered);
    }

    private static DesktopNotificationService Service(Adapter adapter) => new(adapter, [], NullLogger<DesktopNotificationService>.Instance);
    private static MessageNotificationPolicy Policy(DesktopPreferences settings, Reader reader, Visibility? visibility = null) =>
        new(settings, reader, visibility ?? new(), NullLogger<MessageNotificationPolicy>.Instance);
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static MessageNotificationGroup Group(IncomingMessageCategory category = IncomingMessageCategory.Private) => new(
        new(new(Guid.NewGuid(), Guid.NewGuid(), Node, Guid.NewGuid(), 1, true)) { SessionId = Guid.NewGuid(), Category = category }, 1, 1);
    private static CommittedMessageDetails Details(MessageNotificationGroup group, ConversationKind kind = ConversationKind.Contact) =>
        new(group.Latest.Message.NodeId, group.Latest.Message.ConversationId, group.Latest.Message.MessageId, 1,
            kind, "secret name only for known contact", MessageDirection.Incoming, StoredMessageKind.Text, 0, "message");

    private sealed class Settings : ISettingsStore
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Reader : IMessageDetailsReader
    {
        public CommittedMessageDetails? Value; public bool Throw; public TaskCompletionSource? Gate;
        public TaskCompletionSource Started = NewGate();
        public async Task<CommittedMessageDetails?> GetAsync(Guid nodeId, Guid conversationId, Guid messageId, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(); if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
            if (Throw) throw new IOException("message text must not be logged");
            return Value;
        }
    }
    private sealed class Visibility : IMessageNotificationVisibility
    {
        public bool Visible;
        public Task<bool> IsVisibleAsync(MessageNotificationGroup group, CancellationToken cancellationToken) => Task.FromResult(Visible);
    }
    private sealed class Commits : IMessageCommitNotifications
    {
        private EventHandler<IncomingMessageCommitEvent>? _committed;
        public int Subscribers;
        public event EventHandler<IncomingMessageCommitEvent>? MessageCommitted
        { add { _committed += value; Subscribers++; } remove { _committed -= value; Subscribers--; } }
        public void Publish(IncomingMessageCommitEvent commit) => _committed?.Invoke(this, commit);
    }
    private sealed class Sink : IDesktopNotificationService
    {
        public Channel<NotificationRequest> Queue = Channel.CreateUnbounded<NotificationRequest>();
        public IEnumerable<NotificationRequest> Pending { get { while (Queue.Reader.TryRead(out var value)) yield return value; } }
        public bool Submit(NotificationRequest request) => Queue.Writer.TryWrite(request);
        public async Task<NotificationRequest> NextAsync() => await Queue.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
    }
    private sealed class Adapter : IDesktopNotificationAdapter
    {
        private int _calls;
        private readonly Channel<NotificationRequest> _delivered = Channel.CreateUnbounded<NotificationRequest>();
        public TaskCompletionSource? BlockFirst; public bool ThrowFirst;
        public TaskCompletionSource Started = NewGate();
        public IEnumerable<NotificationRequest> Delivered { get { while (_delivered.Reader.TryRead(out var value)) yield return value; } }
        public async Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Started.TrySetResult(); if (BlockFirst is not null) await BlockFirst.Task.WaitAsync(cancellationToken);
                if (ThrowFirst) throw new IOException("private payload should not reach logs");
            }
            cancellationToken.ThrowIfCancellationRequested(); _delivered.Writer.TryWrite(request);
        }
        public async Task<NotificationRequest> NextAsync() => await _delivered.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
    }
    private sealed class Clock : TimeProvider
    {
        private readonly Channel<Timer> _timers = Channel.CreateUnbounded<Timer>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(callback, state); _timers.Writer.TryWrite(timer); return timer; }
        public async Task<Timer> NextAsync() => await _timers.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        public sealed class Timer(TimerCallback callback, object? state) : ITimer
        {
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
