using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    [Theory]
    [InlineData((byte)0, PrivateRepeatMode.SameTimestampIncrementAttempt)]
    [InlineData((byte)0x42, PrivateRepeatMode.SameTimestampIncrementAttempt)]
    [InlineData((byte)0x82, PrivateRepeatMode.NewTimestampResetAttempt)]
    public async Task KnownRouteRetriesThreeTimesThenResetsAndUsesTwoNewFloodAttempts(byte route, PrivateRepeatMode mode)
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 100, route, autoRetries: true);
        var result = await f.Send("known to flood", 0xA1, 1, new(mode));
        var cycle = await WaitCycle(f, result.MessageId, PrivateDeliveryState.Unconfirmed);
        await Until(() => f.Deliveries!.PendingCount == 0);
        Assert.Equal(5, cycle.PlannedAttemptCount);
        Assert.Equal(5, cycle.PreparedAttemptCount);
        Assert.Equal(PrivateDeliveryPhase.FallbackFlood, cycle.CurrentPhase);
        Assert.Equal(RouteRequest(f).PublicKey.ToArray(), Assert.Single(f.Server.RouteResets));
        var wire = f.Server.PrivateTransmissions.ToArray();
        Assert.Equal(new[] {route, route, route, byte.MaxValue, byte.MaxValue}, wire.Select(item => item.RouteDescriptor));
        Assert.Equal(mode == PrivateRepeatMode.SameTimestampIncrementAttempt ? new byte[] {0,1,2,0,1} : [0,0,0,0,0], wire.Select(item => item.Attempt));
        Assert.Equal(5, wire.Select(item => item.ExpectedAck).Distinct().Count());
        if (mode == PrivateRepeatMode.SameTimestampIncrementAttempt)
        {
            Assert.All(wire[..3], item => Assert.Equal(wire[0].Timestamp, item.Timestamp));
            Assert.True(wire[3].Timestamp > wire[0].Timestamp);
            Assert.Equal(wire[3].Timestamp, wire[4].Timestamp);
        }
        else Assert.True(wire.Zip(wire.Skip(1)).All(pair => pair.Second.Timestamp > pair.First.Timestamp));
        var captures = await f.Storage.OutgoingMessages.GetPrivateAttemptCapturesAsync(f.Node, result.MessageId, CancellationToken);
        Assert.Equal(wire.Select(item => item.Timestamp), captures.Select(item => item.WireMessage.Timestamp));
        Assert.Equal(wire.Select(item => item.RouteDescriptor), captures.Select(item => item.Route.Descriptor));
        var message = await f.Storage.OutgoingMessages.GetAsync(f.Node, result.MessageId, CancellationToken);
        var bubble = Assert.Single(await f.Storage.History.GetMessagesAsync(f.Node, message.ConversationId, null, 20, CancellationToken),
            item => item.Direction == MessageDirection.Outgoing);
        Assert.Equal(5, bubble.PrivateDelivery!.AttemptNumber);
        Assert.Equal(byte.MaxValue, (await f.Storage.ConversationDirectory.GetContactDetailsAsync(f.Node, RouteRequest(f).PublicKey, CancellationToken))!.OutPathLength);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AckOfFirstKnownAttemptStopsCycleEvenDuringFallback(int sentCount)
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 250, 0, autoRetries: true);
        var result = await f.Send("late known ACK", 0xA1, 1);
        await Until(() => f.Server.PrivateTransmissions.Count == sentCount);
        await f.Server.SendAcknowledgementAsync(f.Server.PrivateTransmissions.First().ExpectedAck, 50);
        await WaitCycle(f, result.MessageId, PrivateDeliveryState.Delivered);
        await Until(() => f.Deliveries!.PendingCount == 0);
        Assert.Equal(sentCount, f.Server.PrivateTransmissions.Count);
        Assert.Equal(sentCount > 3 ? 1 : 0, f.Server.RouteResets.Count);
        using var connection = SqliteDatabase.CreateConnection(f.Paths.DatabasePath, SqliteOpenMode.ReadOnly);
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT RouteDescriptor FROM ContactDeliveryCandidates;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public async Task AckBeforeFallbackResetPreventsMutationAndFurtherTransmissions()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 100, 0, autoRetries: true);
        Guid message = Guid.Empty;
        var injected = false;
        f.Server.BeforeSingleContactResponse = async () =>
        {
            if (injected || f.Server.PrivateTransmissions.Count != 3) return;
            injected = true;
            await f.Server.SendAcknowledgementAsync(f.Server.PrivateTransmissions.First().ExpectedAck);
            await WaitCycle(f, message, PrivateDeliveryState.Delivered);
        };
        message = (await f.Send("ACK at reset boundary", 0xA1, 1)).MessageId;
        await WaitCycle(f, message, PrivateDeliveryState.Delivered);
        await Until(() => f.Deliveries!.PendingCount == 0);
        Assert.Empty(f.Server.RouteResets);
        Assert.Equal(3, f.Server.PrivateTransmissions.Count);
    }

    [Fact]
    public async Task AckDuringResetReadbackKeepsDeliveredAndDoesNotStartFloodTransmission()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 100, 0, autoRetries: true);
        Guid message = Guid.Empty;
        var injected = false;
        f.Server.BeforeSingleContactResponse = async () =>
        {
            if (injected || f.Server.RouteResets.IsEmpty) return;
            injected = true;
            await f.Server.SendAcknowledgementAsync(f.Server.PrivateTransmissions.First().ExpectedAck);
            await WaitCycle(f, message, PrivateDeliveryState.Delivered);
        };
        message = (await f.Send("ACK during readback", 0xA1, 1)).MessageId;
        await WaitCycle(f, message, PrivateDeliveryState.Delivered);
        await Until(() => f.Deliveries!.PendingCount == 0);
        Assert.Single(f.Server.RouteResets);
        Assert.Equal(3, f.Server.PrivateTransmissions.Count);
        Assert.Equal(byte.MaxValue, (await f.Storage.ConversationDirectory.GetContactDetailsAsync(f.Node, RouteRequest(f).PublicKey, CancellationToken))!.OutPathLength);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedResetOrReadbackStopsCycleWithoutFloodOrMutationReplay(bool readback)
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 100, 0, autoRetries: true);
        if (readback)
            f.Server.BeforeSingleContactResponse = () => { if (!f.Server.RouteResets.IsEmpty) f.Server.RejectContactsReadback = true; return Task.CompletedTask; };
        else f.Server.RejectRouteReset = true;
        var result = await f.Send("failed fallback", 0xA1, 1);
        await WaitCycle(f, result.MessageId, PrivateDeliveryState.Unknown);
        await Until(() => f.Deliveries!.PendingCount == 0);
        Assert.Single(f.Server.RouteResets);
        Assert.Equal(3, f.Server.PrivateTransmissions.Count);
        await f.Writes!.RetryAsync(CancellationToken);
        await f.Writes.FlushAsync(CancellationToken);
        Assert.Single(f.Server.RouteResets);
        Assert.Equal(3, f.Server.PrivateTransmissions.Count);
    }

    [Fact]
    public async Task ResetLocalCommitFailurePausesWithoutRepeatingSuccessfulMutation()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 100, 0, autoRetries: true);
        using var connection = SqliteDatabase.CreateConnection(f.Paths.DatabasePath, SqliteOpenMode.ReadWrite);
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER RejectP5 BEFORE UPDATE OF OutPathLength ON Contacts BEGIN SELECT RAISE(ABORT,'reset local failure'); END;";
        command.ExecuteNonQuery();
        var result = await f.Send("failed route commit", 0xA1, 1);
        await Until(() => f.Deliveries!.PendingCount == 0 && f.Writes!.IsPaused);
        Assert.Equal(3, f.Server.PrivateTransmissions.Count);
        Assert.Single(f.Server.RouteResets);
        command.CommandText = "DROP TRIGGER RejectP5;"; command.ExecuteNonQuery();
        await f.Writes!.RetryAsync(CancellationToken);
        await f.Writes.FlushAsync(CancellationToken);
        await WaitCycle(f, result.MessageId, PrivateDeliveryState.Unknown);
        Assert.Single(f.Server.RouteResets);
        Assert.Equal(3, f.Server.PrivateTransmissions.Count);
    }

    [Fact]
    public async Task FloodPhaseResetsNewlyLearnedKnownRouteBeforeNextTransmission()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 100, autoRetries: true);
        f.Server.LearnRouteOnPrivateSend = true;
        f.Server.LearnedRouteDescriptor = 0;
        var result = await f.Send("route learned without ACK", 0xA1, 1);
        await WaitCycle(f, result.MessageId, PrivateDeliveryState.Unconfirmed);
        await Until(() => f.Deliveries!.PendingCount == 0);
        Assert.Equal(2, f.Server.RouteResets.Count);
        Assert.All(f.Server.PrivateTransmissions, item => Assert.Equal(byte.MaxValue, item.RouteDescriptor));
    }

    [Fact]
    public async Task KnownPhaseCapturesCurrentRouteInsteadOfRestoringInitialPath()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 100, 0x42, autoRetries: true);
        f.Server.LearnRouteOnPrivateSend = true;
        f.Server.LearnedRouteDescriptor = 0;
        var result = await f.Send("changed known path", 0xA1, 1);
        await WaitCycle(f, result.MessageId, PrivateDeliveryState.Unconfirmed);
        await Until(() => f.Deliveries!.PendingCount == 0);
        Assert.Equal(new byte[] {0x42,0,0,255,255}, f.Server.PrivateTransmissions.Select(item => item.RouteDescriptor));
        Assert.Equal(2, f.Server.RouteResets.Count);
    }
    [Fact]
    public async Task ResetReadbackMustConfirmFloodBeforePreparingFallback()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 100, 0, autoRetries: true);
        f.Server.BeforeRouteResetResponse = () => { f.Server.ContactRoutes[0xA1] = 0; return Task.CompletedTask; };
        var result = await f.Send("route immediately restored", 0xA1, 1);
        await WaitCycle(f, result.MessageId, PrivateDeliveryState.Unknown);
        await Until(() => f.Deliveries!.PendingCount == 0);
        Assert.Single(f.Server.RouteResets);
        Assert.Equal(3, f.Server.PrivateTransmissions.Count);
        Assert.Equal(3, (await f.Storage.OutgoingMessages.GetPrivateAttemptCapturesAsync(f.Node, result.MessageId, CancellationToken)).Count);
    }

    [Fact]
    public async Task RouteChangeBetweenReadbackAndSendRecordsActualModeAndStopsRetries()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 100, autoRetries: true);
        // The emulator builds the readback frame before this hook. Mutate firmware state afterward.
        f.Server.BeforeSingleContactResponse = () => { f.Server.ContactRoutes[0xA1] = 0; return Task.CompletedTask; };
        var result = await f.Send("route mode race", 0xA1, 1);
        var cycle = await WaitCycle(f, result.MessageId, PrivateDeliveryState.Unknown);
        await Until(() => f.Deliveries!.PendingCount == 0);
        Assert.Equal("RouteModeChangedBeforeSend", cycle.ErrorCode);
        Assert.Single(f.Server.PrivateTransmissions);
        var capture = Assert.Single(await f.Storage.OutgoingMessages.GetPrivateAttemptCapturesAsync(f.Node, result.MessageId, CancellationToken));
        Assert.Equal(PrivateRouteKind.Flood, capture.Route.Kind);
        using var connection = SqliteDatabase.CreateConnection(f.Paths.DatabasePath, SqliteOpenMode.ReadOnly);
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT ModeReportedByMsgSent FROM SendAttempts WHERE WireMessageId IS NOT NULL;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public async Task CancellationDuringResetStopsBeforeFloodAndKeepsExclusiveGuardUntilJobEnds()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 100, 0, autoRetries: true);
        using var cancel = new CancellationTokenSource();
        var entered = NewGate(); var release = NewGate();
        f.Server.BeforeRouteResetResponse = async () => { entered.TrySetResult(); await release.Task; };
        var result = await f.Send("cancel during reset", 0xA1, 1, callerToken: cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken);
            Assert.Throws<InvalidOperationException>(() => f.Operations.BeginExclusive(f.Node, ConversationKind.Contact, RouteRequest(f).PublicKey));
            await cancel.CancelAsync();
        }
        finally { release.TrySetResult(); }
        await Until(() => f.Deliveries!.PendingCount == 0);
        await WaitCycle(f, result.MessageId, PrivateDeliveryState.Unknown);
        Assert.Single(f.Server.RouteResets);
        Assert.Equal(3, f.Server.PrivateTransmissions.Count);
    }

}
