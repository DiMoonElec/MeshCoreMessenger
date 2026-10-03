using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Protocol;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    private static MessageService Sender(Fixture f, DraftWriteTracker drafts, IOutgoingTextProcessor? processor = null) =>
        new(f.Gateway, f.Storage.OutgoingMessages, f.Storage.Directories, f.Storage.ConversationDirectory,
            drafts, processor ?? new PassthroughOutgoingTextProcessor(), TimeProvider.System, f.Storage.Drafts);

    private static async Task<ChannelSendRequest> Request(Fixture f, DraftWriteTracker drafts, string text = "Тест 👋")
    {
        var recipient = await f.ChannelRecipient();
        var target = new DraftTarget(f.NodeId, null, ConversationKind.Channel, recipient.Identity.ToArray());
        await drafts.LoadTextAsync(target, CancellationToken);
        drafts.Update(target, text, 1);
        return new(f.NodeId, f.Supervisor.Snapshot.SessionId!.Value, f.Supervisor.Snapshot.Generation,
            recipient, new(target, text, 1), new());
    }

    [Fact]
    public async Task ChannelSendCommitsBeforeExactlyOneWireAndUsesProcessedCapture()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        var sender = Sender(f, drafts, new CapturedProcessor());
        var request = await Request(f, drafts);
        var commits = new List<OutgoingMessageCommit>();
        f.Storage.OutgoingMessages.MessageCommitted += (_, c) => commits.Add(c);
        f.Clients.Current!.ChannelSendAction = async () =>
        {
            var c = Assert.Single(commits, c => c.Inserted);
            var attempt = Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, c.MessageId, CancellationToken));
            Assert.Equal(SendAttemptState.Sending, attempt.State);
            var stored = await f.Storage.OutgoingMessages.GetAsync(f.NodeId, c.MessageId, CancellationToken);
            Assert.Equal("Тест 👋", stored.OriginalText); Assert.Equal("processed", stored.TransmissionText);
        };
        var outcome = await sender.SendChannelAsync(request, cancellationToken: CancellationToken);
        Assert.Equal(SendAttemptState.Accepted, outcome.State);
        Assert.Equal(1, f.Clients.Current.Tx); Assert.Equal("processed", f.Clients.Current.SentText);
        var final = Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, outcome.MessageId, CancellationToken));
        Assert.Equal(AckExpectation.NotExpected, final.AckExpectation); Assert.Equal(42, final.WireTimestamp);
        Assert.Single(commits, c => c.Inserted); Assert.Equal(3, commits.Count);
        Assert.Equal("", await drafts.LoadTextAsync(request.Draft.Target, CancellationToken));
        await f.Supervisor.DisconnectAsync(CancellationToken); await f.Connect();
        Assert.Equal(1, f.Clients.All.Sum(c => c.Tx));
    }

    [Theory]
    [InlineData("error", SendAttemptState.Failed)]
    [InlineData("timeout", SendAttemptState.Unknown)]
    [InlineData("disconnect", SendAttemptState.Unknown)]
    public async Task ChannelOutcomeDoesNotInventDeliveryOrRetry(string failure, SendAttemptState expected)
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        f.Clients.Current!.ChannelSendAction = () => Task.FromException(failure switch
        {
            "error" => new MeshCoreCommandException(CommandType.SendChannelTextMessage, null),
            "timeout" => new TimeoutException(),
            _ => new OperationCanceledException(),
        });
        var outcome = await Sender(f, drafts).SendChannelAsync(await Request(f, drafts), cancellationToken: CancellationToken);
        Assert.Equal(expected, outcome.State); Assert.Equal(1, f.Clients.Current.Tx);
        Assert.Equal(expected, Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, outcome.MessageId, CancellationToken)).State);
    }

    [Fact]
    public async Task DoubleClickIsRejectedAndNewDraftDuringWireIsPreserved()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System); var sender = Sender(f, drafts);
        var request = await Request(f, drafts); var gate = NewGate();
        f.Clients.Current!.ChannelSendAction = () => gate.Task;
        var sending = sender.SendChannelAsync(request, cancellationToken: CancellationToken);
        await Until(() => f.Clients.Current.Tx == 1);
        drafts.Update(request.Draft.Target, "новый текст", 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendChannelAsync(request, cancellationToken: CancellationToken));
        gate.SetResult(); await sending;
        Assert.Equal("новый текст", await drafts.LoadTextAsync(request.Draft.Target, CancellationToken));
        Assert.Equal(1, f.Clients.Current.Tx);
    }

    [Theory]
    [InlineData("database")]
    [InlineData("binding")]
    [InlineData("session")]
    [InlineData("node")]
    [InlineData("invalid")]
    public async Task RefusalBeforeAdmissionOrPrepareNeverTransmitsAndPreservesDraft(string failure)
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        var request = await Request(f, drafts, failure == "invalid" ? new string('Я', 160) : "draft");
        if (failure == "database") f.Execute("CREATE TRIGGER d5_fail BEFORE INSERT ON Messages BEGIN SELECT RAISE(FAIL,'disk full'); END;");
        if (failure == "binding") f.Execute("UPDATE ChannelBindings SET UnboundUtc=BoundUtc WHERE Slot=7;");
        if (failure == "session") request = request with { SessionId = Guid.NewGuid() };
        if (failure == "node") request = request with { NodeId = Guid.NewGuid() };
        await Assert.ThrowsAnyAsync<Exception>(() => Sender(f, drafts).SendChannelAsync(request, cancellationToken: CancellationToken));
        Assert.Equal(0, f.Clients.All.Sum(c => c.Tx));
        Assert.Equal(request.Draft.Text, await drafts.LoadTextAsync(request.Draft.Target, CancellationToken));
    }

    [Fact]
    public async Task OldCaptureCannotClearNewerDraftBeforePrepare()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        var request = await Request(f, drafts);
        drafts.Update(request.Draft.Target, "newer", 2);
        await Sender(f, drafts).SendChannelAsync(request, cancellationToken: CancellationToken);
        Assert.Equal("Тест 👋", f.Clients.Current!.SentText);
        Assert.Equal("newer", await drafts.LoadTextAsync(request.Draft.Target, CancellationToken));
    }

    [Fact]
    public async Task BindingRetiredAfterPreparedIsRevalidatedBeforeWire()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        f.Storage.OutgoingMessages.MessageCommitted += (_, commit) =>
        {
            if (commit.Inserted) f.Execute("UPDATE ChannelBindings SET UnboundUtc=BoundUtc WHERE Slot=7;");
        };
        var result = await Sender(f, drafts).SendChannelAsync(await Request(f, drafts), cancellationToken: CancellationToken);
        Assert.Equal(SendAttemptState.Failed, result.State); Assert.Equal(0, f.Clients.Current!.Tx);
    }

    [Fact]
    public async Task CancelAfterInvocationRecordsUnknownBeforeShutdownCompletes()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        using var stop = new CancellationTokenSource();
        f.Clients.Current!.ChannelSendAction = () => Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
        var send = Sender(f, drafts).SendChannelAsync(await Request(f, drafts), cancellationToken: stop.Token);
        await Until(() => f.Clients.Current.Tx == 1);
        var nodeId = f.NodeId;
        stop.Cancel(); var result = await send;
        await f.Supervisor.ShutdownAsync(CancellationToken);
        Assert.Equal(SendAttemptState.Unknown, result.State);
        Assert.Equal(SendAttemptState.Unknown, Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(nodeId, result.MessageId, CancellationToken)).State);
        Assert.Equal(1, f.Clients.Current.Tx);
    }

    [Theory]
    [InlineData(6, SendAttemptState.Failed, 0)]
    [InlineData(1, SendAttemptState.Accepted, 1)]
    public async Task StatusCommitFailurePausesAndRecoveryOnlyPersistsWithoutReplay(int failingState, SendAttemptState finalState, int transmissions)
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var node = f.NodeId; var generation = f.Supervisor.Snapshot.Generation;
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        Guid messageId = Guid.Empty;
        f.Storage.OutgoingMessages.MessageCommitted += (_, commit) => { if (commit.Inserted) messageId = commit.MessageId; };
        var request = await Request(f, drafts);
        f.Execute($"CREATE TRIGGER d5_status BEFORE UPDATE ON SendAttempts WHEN NEW.State={failingState} BEGIN SELECT RAISE(ABORT,'disk full'); END;");
        await Assert.ThrowsAsync<OutgoingPersistenceException>(() => Sender(f, drafts).SendChannelAsync(request, cancellationToken: CancellationToken));
        await Until(() => f.Supervisor.Snapshot.State == ConnectionSupervisorState.NeedsAttention);
        Assert.Equal(transmissions, f.Clients.All.Sum(c => c.Tx));
        Assert.True(f.Outgoing.IsPaused);
        f.Execute("DROP TRIGGER d5_status;");
        await f.Supervisor.ConnectNowAsync(CancellationToken); await f.Online(generation);
        Assert.Equal(finalState, Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(node, messageId, CancellationToken)).State);
        Assert.Equal(transmissions, f.Clients.All.Sum(c => c.Tx));
        Assert.Equal(0, f.Outgoing.PendingCount);
    }

    [Fact]
    public async Task StaleFinalStatusCannotReportAcceptanceWithoutItsCommit()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        var request = await Request(f, drafts);
        Guid messageId = Guid.Empty;
        f.Storage.OutgoingMessages.MessageCommitted += (_, commit) => { if (commit.Inserted) messageId = commit.MessageId; };
        f.Clients.Current!.ChannelSendAction = async () =>
        {
            var attempt = Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, messageId, CancellationToken));
            await f.Storage.OutgoingMessages.TransitionAsync(new(f.NodeId, messageId, attempt.Id, request.SessionId,
                SendAttemptState.Sending, SendAttemptState.Unknown, DateTimeOffset.UtcNow), CancellationToken);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sender(f, drafts).SendChannelAsync(request, cancellationToken: CancellationToken));
        Assert.Equal(SendAttemptState.Unknown, Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, messageId, CancellationToken)).State);
        Assert.Equal(1, f.Clients.Current.Tx);
    }

    private sealed class CapturedProcessor : IOutgoingTextProcessor
    {
        public ProcessedOutgoingText Process(string text, OutgoingTextContext context, OutgoingTextOptions options) =>
            new(text, "processed", MeshCoreSharp.TextMessageValidator.Validate("processed", MeshCoreSharp.TextMessageValidator.GetChannelTextLimit(context.SenderName!)));
    }
}
