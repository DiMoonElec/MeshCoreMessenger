using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    private static async Task Until(Func<Task<bool>> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition()) await Task.Delay(5, deadline.Token);
    }
    private async Task<PrivateDeliveryCycleSnapshot> WaitCycle(TcpPrivateFixture f, Guid message, PrivateDeliveryState state)
    {
        await Until(async () => (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.Node, message, CancellationToken))?.State == state);
        return (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.Node, message, CancellationToken))!;
    }

    [Theory]
    [InlineData(PrivateRepeatMode.SameTimestampIncrementAttempt)]
    [InlineData(PrivateRepeatMode.NewTimestampResetAttempt)]
    public async Task FloodRetryBudgetUsesSelectedWireModeAndKeepsOneBubble(PrivateRepeatMode mode)
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 100, autoRetries: true);
        var result = await f.Send("P4 timeout", 0xA1, 1, new(mode));
        Assert.True(result.Queued);
        Assert.Equal(SendAttemptState.Prepared, result.State);
        var cycle = await WaitCycle(f, result.MessageId, PrivateDeliveryState.Unconfirmed);
        await Until(() => f.Deliveries!.PendingCount == 0);
        Assert.Equal(3, cycle.PreparedAttemptCount);
        Assert.Equal(3, f.Server.PrivateTransmissions.Count);
        var wire = f.Server.PrivateTransmissions.ToArray();
        Assert.Equal(mode == PrivateRepeatMode.NewTimestampResetAttempt ? new byte[] {0,0,0} : [0,1,2], wire.Select(item => item.Attempt));
        Assert.Equal(mode == PrivateRepeatMode.NewTimestampResetAttempt ? 3 : 1, wire.Select(item => item.Timestamp).Distinct().Count());
        Assert.Equal(3, wire.Select(item => item.ExpectedAck).Distinct().Count());
        var message = await f.Storage.OutgoingMessages.GetAsync(f.Node, result.MessageId, CancellationToken);
        var history = Assert.Single(await f.Storage.History.GetMessagesAsync(f.Node, message.ConversationId, null, 20, CancellationToken),
            item => item.Direction == MessageDirection.Outgoing);
        Assert.Equal(PrivateDeliveryState.Unconfirmed, history.PrivateDelivery!.State);
        Assert.Equal(3, (await f.Storage.OutgoingMessages.GetAttemptsAsync(f.Node, result.MessageId, CancellationToken)).Count);
    }

    [Fact]
    public async Task FloodFirstAckStopsJobAndWritesConfiguredRouteAndPcSendTime()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(true, 100, autoRetries: true);
        var result = await f.Send("P4 first ACK", 0xA1, 1);
        await WaitCycle(f, result.MessageId, PrivateDeliveryState.Delivered);
        await Until(() => f.Deliveries!.PendingCount == 0);
        Assert.Single(f.Server.PrivateTransmissions);
        using var connection = SqliteDatabase.CreateConnection(f.Paths.DatabasePath, SqliteOpenMode.ReadOnly);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ContactDeliveryCandidates WHERE SentUtc IS NOT NULL AND PcTimeZoneId IS NOT NULL AND RouteDescriptor=255 AND ModeReportedByMsgSent=1;";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 0)]
    public async Task AckFromAnyAttemptStopsFutureFloodTransmissions(int sentCount, int acknowledgedIndex)
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 250, autoRetries: true);
        var result = await f.Send("P4 late ACK", 0xA1, 1);
        await Until(() => f.Server.PrivateTransmissions.Count == sentCount);
        await f.Server.SendAcknowledgementAsync(f.Server.PrivateTransmissions.ToArray()[acknowledgedIndex].ExpectedAck, 42);
        await WaitCycle(f, result.MessageId, PrivateDeliveryState.Delivered);
        await Until(() => f.Deliveries!.PendingCount == 0);
        Assert.Equal(sentCount, f.Server.PrivateTransmissions.Count);
        Assert.Equal(PrivateDeliveryState.Delivered, (await f.Storage.History.GetMessagesAsync(f.Node,
            (await f.Storage.OutgoingMessages.GetAsync(f.Node, result.MessageId, CancellationToken)).ConversationId, null, 10, CancellationToken))
            .Single(item => item.Id == result.MessageId).PrivateDelivery!.State);
    }

    [Fact]
    public async Task TwoMessagesToSameContactStayFifoAndRetainDistinctWireIdentities()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 100, autoRetries: true);
        var first = await f.Send("first", 0xA1, 1);
        var second = await f.Send("second", 0xA1, 2);
        await WaitCycle(f, second.MessageId, PrivateDeliveryState.Unconfirmed);
        await WaitCycle(f, first.MessageId, PrivateDeliveryState.Unconfirmed);
        await Until(() => f.Deliveries!.PendingCount == 0);
        var wire = f.Server.PrivateTransmissions.ToArray();
        Assert.Equal(new[] {"first","first","first","second","second","second"}, wire.Select(item => item.Text));
        Assert.True(wire[3].Timestamp > wire[0].Timestamp);
        Assert.Equal(6, wire.Select(item => item.ExpectedAck).Distinct().Count());
    }

    [Fact]
    public async Task DifferentContactsContinueWhileFirstContactWaitsForAck()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 5000, autoRetries: true);
        var first = await f.Send("first contact", 0xA1, 1);
        await Until(() => f.Server.PrivateTransmissions.Count == 1);
        var second = await f.Send("other contact", 0xB2, 2);
        await Until(() => f.Server.PrivateTransmissions.Count == 2);
        Assert.Equal(PrivateDeliveryState.Active, (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.Node, first.MessageId, CancellationToken))!.State);
        // Complete ACKs in reverse order while the two jobs coexist.
        var wire = f.Server.PrivateTransmissions.ToArray();
        await f.Server.SendAcknowledgementAsync(wire[1].ExpectedAck);
        await WaitCycle(f, second.MessageId, PrivateDeliveryState.Delivered);
        await f.Server.SendAcknowledgementAsync(wire[0].ExpectedAck);
        await WaitCycle(f, first.MessageId, PrivateDeliveryState.Delivered);
        Assert.Equal(2, f.Server.PrivateTransmissions.Count);
    }

    [Fact]
    public async Task DisconnectStopsActiveAndQueuedJobsWithoutReplay()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 5000, autoRetries: true);
        var first = await f.Send("active", 0xA1, 1);
        await Until(() => f.Server.PrivateTransmissions.Count == 1);
        var second = await f.Send("queued", 0xA1, 2);
        await f.Supervisor.DisconnectAsync(CancellationToken);
        await Until(() => f.Deliveries!.PendingCount == 0);
        Assert.Single(f.Server.PrivateTransmissions);
        Assert.Equal(SendAttemptState.Failed, (await f.Storage.OutgoingMessages.GetAttemptsAsync(f.Node, second.MessageId, CancellationToken)).Single().State);
        Assert.Single(f.Server.PrivateTransmissions);
        // The emulator serves one TCP connection; use a fresh endpoint exposing the same own public key.
        await using var replacement = new FakeCompanionServer { AutoAcknowledgePrivate = true, UseProtocolPrivateAckTags = true };
        replacement.Start();
        var profile = (await f.Storage.ConnectionProfiles.GetAllAsync(CancellationToken)).Single();
        await f.Storage.ConnectionProfiles.SaveAsync(profile with { TcpPort = replacement.Port }, CancellationToken);
        await f.Supervisor.ConnectNowAsync(CancellationToken);
        await Until(() => f.Supervisor.Snapshot.State == ConnectionSupervisorState.Online);
        Assert.Empty(replacement.PrivateTransmissions);
        var fresh = await f.Send("new explicit message", 0xA1, 3);
        await WaitCycle(f, fresh.MessageId, PrivateDeliveryState.Delivered);
        Assert.Single(replacement.PrivateTransmissions);
        await f.Supervisor.DisconnectAsync(CancellationToken);
    }

    [Fact]
    public async Task ChannelCommandIsNotBlockedByPrivateAckWait()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 5000, autoRetries: true);
        var sent = await f.Send("private wait", 0xA1, 1);
        await Until(() => f.Server.PrivateTransmissions.Count == 1);
        var fingerprint = System.Security.Cryptography.SHA256.HashData(Enumerable.Repeat((byte)3,
            MeshCoreSharp.Protocol.ProtocolLimits.ChannelSecretSize).ToArray());
        var recipient = (await f.Sender.GetChannelTargetsAsync(f.Node, fingerprint, CancellationToken)).Single();
        var target = new DraftTarget(f.Node, null, ConversationKind.Channel, fingerprint);
        await f.Drafts.LoadTextAsync(target, CancellationToken);
        f.Drafts.Update(target, "channel alongside private", 1);
        var owner = f.Supervisor.Snapshot;
        var channel = await f.Sender.SendChannelAsync(new(f.Node, owner.SessionId!.Value, owner.Generation, recipient,
            new(target, "channel alongside private", 1), new()), cancellationToken: CancellationToken);
        Assert.Equal(SendAttemptState.Accepted, channel.State);
        Assert.Equal(PrivateDeliveryState.Active, (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.Node, sent.MessageId, CancellationToken))!.State);
        await f.Server.SendAcknowledgementAsync(f.Server.PrivateTransmissions.Single().ExpectedAck);
        await WaitCycle(f, sent.MessageId, PrivateDeliveryState.Delivered);
    }

    [Fact]
    public async Task CancellingMiddleQueuedJobDoesNotLetItsSuccessorOvertakeActiveJob()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 5000, autoRetries: true);
        var first = await f.Send("first", 0xA1, 1);
        await Until(() => f.Server.PrivateTransmissions.Count == 1);
        using var stop = new CancellationTokenSource();
        var cancelled = await f.Send("cancelled", 0xA1, 2, callerToken: stop.Token);
        await stop.CancelAsync();
        await Until(() => f.Deliveries!.PendingCount == 1);
        var third = await f.Send("third", 0xA1, 3);
        Assert.Single(f.Server.PrivateTransmissions);
        await f.Server.SendAcknowledgementAsync(f.Server.PrivateTransmissions.Single().ExpectedAck);
        await WaitCycle(f, first.MessageId, PrivateDeliveryState.Delivered);
        await Until(() => f.Server.PrivateTransmissions.Count == 2);
        Assert.Equal("third", f.Server.PrivateTransmissions.Last().Text);
        await f.Server.SendAcknowledgementAsync(f.Server.PrivateTransmissions.Last().ExpectedAck);
        await WaitCycle(f, third.MessageId, PrivateDeliveryState.Delivered);
        Assert.Equal(SendAttemptState.Failed, (await f.Storage.OutgoingMessages.GetAttemptsAsync(f.Node, cancelled.MessageId, CancellationToken)).Single().State);
    }

    [Fact]
    public async Task QueueCapacityRejectsAdmissionBeforeDraftTransferOrExtraPreparedMessage()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 5000, autoRetries: true);
        for (var index = 0; index < PrivateDeliveryCoordinator.MaximumJobsPerContact; index++)
            await f.Send($"queued {index}", 0xA1, index + 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Send("keep this draft", 0xA1, 9));
        var key = Enumerable.Repeat((byte)0xA1, 32).ToArray();
        var target = new DraftTarget(f.Node, null, ConversationKind.Contact, key);
        Assert.Equal("keep this draft", await f.Drafts.LoadTextAsync(target, CancellationToken));
        Assert.Equal(PrivateDeliveryCoordinator.MaximumJobsPerContact, f.Deliveries!.PendingCount);
        Assert.Throws<InvalidOperationException>(() => f.Operations.BeginExclusive(f.Node, ConversationKind.Contact, key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitNodeErrorOrMissingExpectedAckNeverTriggersAutomaticRetry(bool noAck)
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 100, autoRetries: true);
        f.Server.RejectPrivateSend = !noAck;
        if (noAck) { f.Server.UseProtocolPrivateAckTags = false; f.Server.PrivateAckTag = _ => 0; }
        var result = await f.Send("stop without timeout", 0xA1, 1);
        await WaitCycle(f, result.MessageId, PrivateDeliveryState.Failed);
        await Until(() => f.Deliveries!.PendingCount == 0);
        Assert.True(f.Server.PrivateTransmissions.Count <= 1);
    }

    [Fact]
    public async Task GlobalQueueBoundIsReleasedWithoutSendingOrCreatingHistory()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var coordinator = new PrivateDeliveryCoordinator(f.Storage.OutgoingMessages, f.Outgoing, TimeProvider.System);
        var leases = new List<SessionCommandLease>();
        var reservations = new List<PrivateDeliveryCoordinator.Reservation>();
        try
        {
            for (var index = 0; index <= PrivateDeliveryCoordinator.MaximumJobs; index++)
            {
                var lease = f.Gateway.Acquire(f.NodeId, new ContactCommandTarget(Enumerable.Repeat((byte)index, 32).ToArray()), CancellationToken);
                leases.Add(lease);
                if (index == PrivateDeliveryCoordinator.MaximumJobs)
                    Assert.Throws<InvalidOperationException>(() => coordinator.Reserve(lease));
                else reservations.Add(coordinator.Reserve(lease));
            }
            Assert.Equal(PrivateDeliveryCoordinator.MaximumJobs, coordinator.PendingCount);
            Assert.Equal(0, f.Clients.Current!.Tx);
        }
        finally
        {
            foreach (var reservation in reservations) reservation.Dispose();
            foreach (var lease in leases) await lease.DisposeAsync();
        }
        Assert.Equal(0, coordinator.PendingCount);
    }

    [Fact]
    public async Task VirtualTimeoutsExecuteExactlyThreeAttemptsWithoutWallClockDelays()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var time = new RetryTestTimeProvider();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, time);
        var coordinator = new PrivateDeliveryCoordinator(f.Storage.OutgoingMessages, f.Outgoing, time);
        var sender = new MessageService(f.Gateway, f.Storage.OutgoingMessages, f.Storage.Directories,
            f.Storage.ConversationDirectory, drafts, new PassthroughOutgoingTextProcessor(), time, f.Storage.Drafts, f.Operations, coordinator);
        f.Clients.Current!.CapturedPrivateSendAction = (timestamp, attempt, token) => Task.FromResult(new MeshCoreSharp.Models.TextMessageSendResult(
            timestamp, new(true, (uint)attempt + 1, 1000), TimeoutAsync(token)));
        async Task<MeshCoreSharp.Models.MessageDeliveryResult> TimeoutAsync(CancellationToken token)
        {
            await Task.Delay(TimeSpan.FromHours(1), time, token);
            return new(MeshCoreSharp.Models.MessageDeliveryStatus.TimedOut, null);
        }
        var request = await PrivateRequest(f, drafts, "virtual retries");
        var result = await sender.SendPrivateAsync(request, cancellationToken: CancellationToken);
        for (var index = 0; index < 3; index++)
        {
            await Until(() => f.Clients.Current.Tx == index + 1 && time.HasTimer);
            time.Advance();
        }
        await Until(() => coordinator.PendingCount == 0);
        Assert.Equal(3, f.Clients.Current.Tx);
        Assert.Equal(PrivateDeliveryState.Unconfirmed, (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.NodeId, result.MessageId, CancellationToken))!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistencePauseDoesNotReplayPreparationOrAcceptedRadioCommand(bool afterTx)
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 5000, autoRetries: true);
        using (var connection = SqliteDatabase.CreateConnection(f.Paths.DatabasePath, SqliteOpenMode.ReadWrite))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = afterTx
                ? "CREATE TRIGGER RejectP4 BEFORE UPDATE ON SendAttempts WHEN NEW.State=1 BEGIN SELECT RAISE(ABORT,'accepted failure'); END;"
                : "CREATE TRIGGER RejectP4 BEFORE INSERT ON PrivateWireMessages BEGIN SELECT RAISE(ABORT,'prepare failure'); END;";
            command.ExecuteNonQuery();
        }
        var result = await f.Send("persistence stop", 0xA1, 1);
        await Until(() => f.Deliveries!.PendingCount == 0 && f.Writes!.IsPaused);
        Assert.Equal(afterTx ? 1 : 0, f.Server.PrivateTransmissions.Count);
        using (var connection = SqliteDatabase.CreateConnection(f.Paths.DatabasePath, SqliteOpenMode.ReadWrite))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER RejectP4;"; command.ExecuteNonQuery();
        }
        await f.Writes!.RetryAsync(CancellationToken);
        await f.Writes.FlushAsync(CancellationToken);
        Assert.Equal(afterTx ? 1 : 0, f.Server.PrivateTransmissions.Count);
        Assert.Equal(PrivateDeliveryState.Unknown, (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.Node, result.MessageId, CancellationToken))!.State);
    }

    [Fact]
    public async Task KnownRouteKeepsOneSendBaselineUntilOwnedResetStage()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 100, initialRoute: 0, autoRetries: true);
        var result = await f.Send("known route", 0xA1, 1);
        await Until(async () => (await f.Storage.OutgoingMessages.GetAttemptsAsync(f.Node, result.MessageId, CancellationToken)).Single().State == SendAttemptState.Unconfirmed);
        await Until(() => f.Deliveries!.PendingCount == 0);
        Assert.Single(f.Server.PrivateTransmissions);
        Assert.Null(await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.Node, result.MessageId, CancellationToken));
    }

    private sealed class RetryTestTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _utc = DateTimeOffset.UtcNow;
        private readonly List<TestTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utc; }
        public bool HasTimer { get { lock (_gate) return _timers.Count != 0; } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TestTimer(callback, state, this);
            lock (_gate) _timers.Add(timer);
            return timer;
        }
        public void Advance()
        {
            TestTimer[] timers;
            lock (_gate) { _utc = _utc.AddHours(1); timers = _timers.ToArray(); _timers.Clear(); }
            foreach (var timer in timers) timer.Fire();
        }
        private sealed class TestTimer(TimerCallback callback, object? state, RetryTestTimeProvider owner) : ITimer
        {
            private int _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;
            public void Fire() { if (Volatile.Read(ref _disposed) == 0) callback(state); }
            public void Dispose() { Interlocked.Exchange(ref _disposed, 1); lock (owner._gate) owner._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
