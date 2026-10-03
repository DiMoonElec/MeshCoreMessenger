using System.Buffers.Binary;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    private static async Task<PrivateSendRequest> PrivateRequest(Fixture f, DraftWriteTracker drafts, string text = "Личное 👋", long revision = 1)
    {
        var target = new DraftTarget(f.NodeId, null, ConversationKind.Contact, f.ContactKey.ToArray());
        await drafts.LoadTextAsync(target, CancellationToken);
        drafts.Update(target, text, revision);
        return new(f.NodeId, f.Supervisor.Snapshot.SessionId!.Value, f.Supervisor.Snapshot.Generation,
            new(ConversationKind.Contact, f.ContactKey.ToArray()), new(target, text, revision), new());
    }

    private static TaskCompletionSource<MessageDeliveryResult> DeliveryGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<OutgoingAttemptSnapshot> Attempt(Fixture f, Guid node, Guid message) =>
        Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(node, message, CancellationToken));
    private static async Task WaitState(Fixture f, Guid node, Guid message, SendAttemptState state)
    {
        var stopAt = DateTime.UtcNow.AddSeconds(5);
        while ((await Attempt(f, node, message)).State != state)
        {
            if (DateTime.UtcNow > stopAt) throw new TimeoutException($"Expected {state} for {message}.");
            await Task.Delay(10, CancellationToken);
        }
    }

    [Fact]
    public async Task PrivateSendUsesProcessorCaptureWithFull160ByteBudget()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        var request = await PrivateRequest(f, drafts, "оригинал");
        var sender = Sender(f, drafts, new PrivateProcessor());
        var result = await sender.SendPrivateAsync(request, cancellationToken: CancellationToken);
        var stored = await f.Storage.OutgoingMessages.GetAsync(f.NodeId, result.MessageId, CancellationToken);
        Assert.Equal("оригинал", stored.OriginalText);
        Assert.Equal(new string('я', 80), stored.TransmissionText);
        Assert.Equal(stored.TransmissionText, f.Clients.Current!.SentText);
        Assert.Equal(1, f.Clients.Current.Tx);
    }

    private sealed class PrivateProcessor : IOutgoingTextProcessor
    {
        public ProcessedOutgoingText Process(string text, OutgoingTextContext context, OutgoingTextOptions options)
        {
            Assert.False(context.IsChannel);
            var transmission = new string('я', 80);
            return new(text, transmission, MeshCoreSharp.TextMessageValidator.Validate(transmission, ProtocolLimits.MaxTextBytes));
        }
    }

    [Theory]
    [InlineData(MessageDeliveryStatus.Confirmed, SendAttemptState.Delivered)]
    [InlineData(MessageDeliveryStatus.TimedOut, SendAttemptState.Unconfirmed)]
    [InlineData(MessageDeliveryStatus.NotExpected, SendAttemptState.Accepted)]
    public async Task ImmediatePrivateDeliveryCommitsAcceptedBeforeTerminalAndCapturesMetadata(MessageDeliveryStatus status, SendAttemptState expected)
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var node = f.NodeId; var generation = f.Supervisor.Snapshot.Generation;
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        var request = await PrivateRequest(f, drafts);
        var states = new System.Collections.Concurrent.ConcurrentQueue<SendAttemptState>();
        f.Storage.OutgoingMessages.MessageCommitted += (_, c) =>
        {
            using var connection = MeshCoreMessenger.Core.Persistence.Sqlite.SqliteDatabase.CreateConnection(f.AppPaths.DatabasePath, Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly);
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT State FROM SendAttempts WHERE MessageId=$id;";
            command.Parameters.AddWithValue("$id", c.MessageId.ToString("D"));
            states.Enqueue((SendAttemptState)Convert.ToInt32(command.ExecuteScalar()));
        };
        const uint tag = 0xAB123456;
        f.Clients.Current!.PrivateSendAction = async _ =>
        {
            Assert.Equal(new[] { SendAttemptState.Prepared, SendAttemptState.Sending }, states.ToArray());
            await Task.Yield();
            return new(43, new(false, status == MessageDeliveryStatus.NotExpected ? 0 : tag, 100),
                Task.FromResult(new MessageDeliveryResult(status, status == MessageDeliveryStatus.Confirmed ? new(tag, 77) : null)));
        };
        var outcome = await Sender(f, drafts).SendPrivateAsync(request, cancellationToken: CancellationToken);
        await WaitState(f, node, outcome.MessageId, expected);
        Assert.Equal(1, f.Clients.Current.Tx);
        Assert.Equal(request.Draft.Text, f.Clients.Current.SentText);
        var attempt = await Attempt(f, node, outcome.MessageId);
        Assert.Equal(43, attempt.WireTimestamp);
        Assert.Equal(expected == SendAttemptState.Accepted ? AckExpectation.NotExpected : AckExpectation.Expected, attempt.AckExpectation);
        Assert.Equal(expected == SendAttemptState.Delivered ? 77 : (int?)null, attempt.RoundTripMilliseconds);
        if (status != MessageDeliveryStatus.NotExpected) Assert.Equal(tag, BinaryPrimitives.ReadUInt32LittleEndian(attempt.ExpectedAck!.Value.Span));
        Assert.Equal("", await drafts.LoadTextAsync(request.Draft.Target, CancellationToken));
        Assert.Equal(expected == SendAttemptState.Accepted
            ? new[] { SendAttemptState.Prepared, SendAttemptState.Sending, SendAttemptState.Accepted }
            : new[] { SendAttemptState.Prepared, SendAttemptState.Sending, SendAttemptState.Accepted, expected }, states.ToArray());
        Assert.Equal(generation, f.Supervisor.Snapshot.Generation);
        Assert.Equal(ConnectionSupervisorState.Online, f.Supervisor.Snapshot.State);
    }

    [Fact]
    public async Task SecondPrivateSendDoesNotWaitForFirstAckAndReverseCompletionUpdatesExactAttempt()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var node = f.NodeId; var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System); var sender = Sender(f, drafts);
        var first = DeliveryGate(); var second = DeliveryGate(); var count = 0;
        f.Clients.Current!.PrivateSendAction = _ =>
        {
            var number = ++count;
            return Task.FromResult(new TextMessageSendResult((uint)number, new(false, (uint)number, 1000), number == 1 ? first.Task : second.Task));
        };
        var a = await sender.SendPrivateAsync(await PrivateRequest(f, drafts, "same", 1), cancellationToken: CancellationToken);
        var b = await sender.SendPrivateAsync(await PrivateRequest(f, drafts, "same", 2), cancellationToken: CancellationToken);
        Assert.Equal(2, f.Clients.Current.Tx); Assert.NotEqual(a.MessageId, b.MessageId);
        second.SetResult(new(MessageDeliveryStatus.Confirmed, new(2, 222)));
        await WaitState(f, node, b.MessageId, SendAttemptState.Delivered);
        Assert.Equal(SendAttemptState.Accepted, (await Attempt(f, node, a.MessageId)).State);
        first.SetResult(new(MessageDeliveryStatus.Confirmed, new(1, 111)));
        await WaitState(f, node, a.MessageId, SendAttemptState.Delivered);
        Assert.Equal(111, (await Attempt(f, node, a.MessageId)).RoundTripMilliseconds);
        Assert.Equal(222, (await Attempt(f, node, b.MessageId)).RoundTripMilliseconds);
    }

    [Theory]
    [InlineData("mismatch")]
    [InlineData("unexpected")]
    [InlineData("error")]
    public async Task InvalidOrFaultedDeliveryNeverClaimsConfirmation(string mode)
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        f.Clients.Current!.PrivateSendAction = _ => Task.FromResult(new TextMessageSendResult(42, new(false, 1, 100), mode switch
        {
            "error" => Task.FromException<MessageDeliveryResult>(new MeshCoreTransportException("lost")),
            "unexpected" => Task.FromResult(new MessageDeliveryResult(MessageDeliveryStatus.NotExpected, null)),
            _ => Task.FromResult(new MessageDeliveryResult(MessageDeliveryStatus.Confirmed, new(2, 10))),
        }));
        var result = await Sender(f, drafts).SendPrivateAsync(await PrivateRequest(f, drafts), cancellationToken: CancellationToken);
        await WaitState(f, f.NodeId, result.MessageId, SendAttemptState.Unknown);
        Assert.Equal(1, f.Clients.Current.Tx);
    }

    [Theory]
    [InlineData("error", SendAttemptState.Failed)]
    [InlineData("timeout", SendAttemptState.Unknown)]
    [InlineData("cancel", SendAttemptState.Unknown)]
    public async Task PrivateImmediateFailureHasHonestStateAndNoRetry(string mode, SendAttemptState expected)
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        f.Clients.Current!.PrivateSendAction = _ => Task.FromException<TextMessageSendResult>(mode switch
        {
            "error" => new MeshCoreCommandException(CommandType.SendTextMessage, null),
            "cancel" => new OperationCanceledException(),
            _ => new TimeoutException(),
        });
        var result = await Sender(f, drafts).SendPrivateAsync(await PrivateRequest(f, drafts), cancellationToken: CancellationToken);
        Assert.Equal(expected, result.State); Assert.Equal(expected, (await Attempt(f, f.NodeId, result.MessageId)).State);
        Assert.Equal(1, f.Clients.Current.Tx);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("nonChat")]
    [InlineData("collision")]
    [InlineData("session")]
    [InlineData("oversize")]
    [InlineData("storage")]
    public async Task PrivateRefusalBeforePreparePreservesDraftAndSendsNothing(string mode)
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        var request = await PrivateRequest(f, drafts, mode == "oversize" ? new string('я', 81) : "draft");
        if (mode == "absent") f.Execute("UPDATE Contacts SET PresentOnNode=0;");
        if (mode == "nonChat") f.Execute("UPDATE Contacts SET ContactType=2;");
        if (mode == "collision")
        {
            var other = f.ContactKey.ToArray(); other[^1]++;
            await f.Storage.Directories.ApplySnapshotAsync(f.NodeId, request.SessionId,
                [new(f.ContactKey, "Peer", 1, 0, new byte[64], DateTimeOffset.UtcNow, 0, 0),
                 new(other, "Repeater collision", 2, 0, new byte[64], DateTimeOffset.UtcNow, 0, 0)],
                [new(7, "channel", System.Security.Cryptography.SHA256.HashData(f.Clients.Secret), ChannelAccessKind.Unknown)],
                DateTimeOffset.UtcNow, CancellationToken);
        }
        if (mode == "session") request = request with { SessionId = Guid.NewGuid() };
        if (mode == "storage") f.Execute("CREATE TRIGGER d6_prepare BEFORE INSERT ON Messages BEGIN SELECT RAISE(FAIL,'full'); END;");
        await Assert.ThrowsAnyAsync<Exception>(() => Sender(f, drafts).SendPrivateAsync(request, cancellationToken: CancellationToken));
        Assert.Equal(0, f.Clients.All.Sum(c => c.Tx));
        Assert.Equal(request.Draft.Text, await drafts.LoadTextAsync(request.Draft.Target, CancellationToken));
    }

    [Fact]
    public async Task ContactRemovedAfterPrepareIsRecheckedBeforeTransmission()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        f.Storage.OutgoingMessages.MessageCommitted += (_, c) => { if (c.Inserted) f.Execute("UPDATE Contacts SET PresentOnNode=0;"); };
        var result = await Sender(f, drafts).SendPrivateAsync(await PrivateRequest(f, drafts), cancellationToken: CancellationToken);
        Assert.Equal(SendAttemptState.Failed, result.State); Assert.Equal(0, f.Clients.Current!.Tx);
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("switch")]
    [InlineData("shutdown")]
    [InlineData("cancel")]
    public async Task PendingPrivateAckIsOwnedThroughLifecycleAndLateOldResultCannotChangeNewSession(string action)
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var node = f.NodeId; var generation = f.Supervisor.Snapshot.Generation;
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System); var delivery = DeliveryGate();
        using var cancel = new CancellationTokenSource();
        f.Clients.Current!.PrivateSendAction = _ => Task.FromResult(new TextMessageSendResult(42, new(false, 1, 1000), delivery.Task));
        var result = await Sender(f, drafts).SendPrivateAsync(await PrivateRequest(f, drafts), cancellationToken: cancel.Token);
        switch (action)
        {
            case "switch": await f.Supervisor.SwitchProfileAsync(f.SecondProfile.Id, CancellationToken); await f.Online(generation); break;
            case "shutdown": await f.Supervisor.ShutdownAsync(CancellationToken); break;
            case "cancel": cancel.Cancel(); break;
            default: await f.Supervisor.DisconnectAsync(CancellationToken); await f.Connect(); break;
        }
        await WaitState(f, node, result.MessageId, SendAttemptState.Unknown);
        delivery.SetResult(new(MessageDeliveryStatus.Confirmed, new(1, 55)));
        await Task.Delay(25, CancellationToken);
        Assert.Equal(SendAttemptState.Unknown, (await Attempt(f, node, result.MessageId)).State);
        Assert.Equal(1, f.Clients.All.Sum(c => c.Tx));
    }

    [Theory]
    [InlineData(1, SendAttemptState.Unknown)]
    [InlineData(2, SendAttemptState.Delivered)]
    public async Task PrivateStatusWriteFailureRetriesOnlyDatabaseAndPreservesOrdering(int failingState, SendAttemptState finalState)
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var node = f.NodeId; var generation = f.Supervisor.Snapshot.Generation;
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System); var delivery = DeliveryGate();
        f.Clients.Current!.PrivateSendAction = _ => Task.FromResult(new TextMessageSendResult(42, new(false, 1, 1000), delivery.Task));
        Guid message = Guid.Empty;
        f.Storage.OutgoingMessages.MessageCommitted += (_, c) => { if (c.Inserted) message = c.MessageId; };
        f.Execute($"CREATE TRIGGER d6_status BEFORE UPDATE ON SendAttempts WHEN NEW.State={failingState} BEGIN SELECT RAISE(ABORT,'full'); END;");
        var send = Sender(f, drafts).SendPrivateAsync(await PrivateRequest(f, drafts), cancellationToken: CancellationToken);
        if (failingState == 1) await Assert.ThrowsAsync<OutgoingPersistenceException>(() => send);
        else { await send; delivery.SetResult(new(MessageDeliveryStatus.Confirmed, new(1, 17))); }
        await Until(() => f.Supervisor.Snapshot.State == ConnectionSupervisorState.NeedsAttention);
        Assert.True(f.Outgoing.IsPaused);
        f.Execute("DROP TRIGGER d6_status;");
        await f.Supervisor.ConnectNowAsync(CancellationToken); await f.Online(generation);
        var attempt = await Attempt(f, node, message);
        Assert.Equal(finalState, attempt.State); Assert.NotNull(attempt.AcceptedUtc);
        Assert.Equal(1, f.Clients.All.Sum(c => c.Tx)); Assert.Equal(0, f.Outgoing.PendingCount);
    }
}
