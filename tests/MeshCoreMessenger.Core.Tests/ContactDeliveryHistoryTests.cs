using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class OutgoingMessageStoreTests
{
    private static byte[] DeliveryContactKey => Enumerable.Repeat((byte)2, 32).ToArray();
    private static async Task<DeliveryRouteReadbackRequest> ConfirmForReadback(Fixture f, uint tag = 1, DateTimeOffset? ackTime = null)
    {
        DeliveryRouteReadbackRequest? request = null;
        void OnCommit(object? sender, OutgoingMessageCommit commit) { request ??= commit.RouteReadback; }
        f.Storage.OutgoingMessages.MessageCommitted += OnCommit;
        try { await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(new(f.NodeId, f.SessionId, tag, uint.MaxValue,
            ackTime ?? PrivatePcTime, -300, "America/New_York"), CancellationToken); }
        finally { f.Storage.OutgoingMessages.MessageCommitted -= OnCommit; }
        return Assert.IsType<DeliveryRouteReadbackRequest>(request);
    }

    [Fact]
    public async Task ReadApiKeepsConfiguredAndReadbackRoutesSeparateWithRealClockAndZone()
    {
        await using var f = await Fixture.CreateAsync();
        var message = await BeginCycle(f);
        var first = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(PrivatePreparation(f, message.MessageId), CancellationToken);
        var time = PrivatePcTime.AddSeconds(1);
        var sent = new OutgoingAttemptTransition(f.NodeId, message.MessageId, first.Message.Attempt.Id, f.SessionId,
            SendAttemptState.Prepared, SendAttemptState.Sending, time, PcSentTime: time, PcSentTimeZoneId: "Europe/Moscow");
        await f.Storage.OutgoingMessages.TransitionAsync(sent, CancellationToken);
        await f.Storage.OutgoingMessages.TransitionAsync(sent with { ExpectedState = SendAttemptState.Sending, State = SendAttemptState.Accepted,
            WireTimestamp = first.Capture.WireMessage.Timestamp, AckExpectation = AckExpectation.Expected, ExpectedAck = new byte[]{1,0,0,0}, ModeReportedByMsgSent = true }, CancellationToken);
        // ACK wall clock moved backwards; readback is ordered by the committed evidence, not wall time.
        var ackTime = PrivatePcTime.AddDays(-1);
        var target = await ConfirmForReadback(f, ackTime: ackTime);
        var learned = new LearnedDeliveryRoute(target, new(0, ReadOnlyMemory<byte>.Empty, ackTime.AddSeconds(-1)));
        Assert.False(await f.Storage.OutgoingMessages.EnrichDeliveryRouteAsync(learned with { Target = target with { SessionId = Guid.NewGuid() } }, CancellationToken));
        Assert.True(await f.Storage.OutgoingMessages.EnrichDeliveryRouteAsync(learned, CancellationToken));
        Assert.False(await f.Storage.OutgoingMessages.EnrichDeliveryRouteAsync(learned with { Route = Route(false) }, CancellationToken));
        var record = Assert.Single((await f.Storage.ContactDeliveries.GetPageAsync(f.NodeId, DeliveryContactKey, cancellationToken: CancellationToken)).Items);
        Assert.Equal(ackTime.ToUniversalTime(), record.FirstAckReceivedUtc);
        Assert.Equal(-300, record.PcUtcOffsetMinutes);
        Assert.Equal("America/New_York", record.PcTimeZoneId);
        var evidence = Assert.Single(record.Evidence);
        Assert.Equal(uint.MaxValue, evidence.RoundTripMilliseconds);
        Assert.Equal(PrivateRouteKind.Direct, evidence.LearnedRoute!.Kind);
        Assert.Equal(DeliveryRouteProvenance.ContactReadbackAfterAcknowledgement, evidence.LearnedRouteProvenance);
        var candidate = Assert.Single(evidence.Candidates);
        Assert.Equal(PrivateRouteKind.Flood, candidate.ConfiguredRoute!.Kind);
        Assert.Equal(time.ToUniversalTime(), candidate.SentUtc);
        Assert.Equal(180, candidate.PcUtcOffsetMinutes);
        Assert.Equal("Europe/Moscow", candidate.PcTimeZoneId);
        Assert.True(candidate.ModeReportedByMsgSent);
    }

    [Fact]
    public async Task HistoryPagesUseScopedTieBreakerAndRemainAfterClearContactRemovalAndReopen()
    {
        await using var f = await Fixture.CreateAsync();
        for (var index = 0; index < 3; index++)
        {
            var message = await BeginCycle(f);
            var prepared = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(PrivatePreparation(f, message.MessageId), CancellationToken);
            await Expire(f, prepared);
            // Retag each message to avoid cross-message ACK collisions in this test.
            using (var connection = f.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE SendAttempts SET ExpectedAck=$tag WHERE Id=$id;";
                command.Parameters.AddWithValue("$tag", new byte[]{(byte)(index+1),0,0,0});
                command.Parameters.AddWithValue("$id", prepared.Message.Attempt.Id.ToString("D"));
                command.ExecuteNonQuery();
            }
            var target = await ConfirmForReadback(f, (uint)index + 1);
            Assert.True(await f.Storage.OutgoingMessages.EnrichDeliveryRouteAsync(new(target, Route(false)), CancellationToken));
        }
        var first = await f.Storage.ContactDeliveries.GetPageAsync(f.NodeId, DeliveryContactKey, 2, cancellationToken: CancellationToken);
        Assert.Equal(2, first.Items.Count); Assert.NotNull(first.NextCursor);
        var second = await f.Storage.ContactDeliveries.GetPageAsync(f.NodeId, DeliveryContactKey, 2, first.NextCursor, CancellationToken);
        Assert.Single(second.Items); Assert.Null(second.NextCursor);
        Assert.Equal(3, first.Items.Concat(second.Items).Select(item => item.Id).Distinct().Count());
        await Assert.ThrowsAsync<ArgumentException>(() => f.Storage.ContactDeliveries.GetPageAsync(Guid.NewGuid(), DeliveryContactKey, 2, first.NextCursor, CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Storage.ContactDeliveries.GetPageAsync(f.NodeId, new byte[32], 2, first.NextCursor, CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Storage.ContactDeliveries.GetPageAsync(f.NodeId, DeliveryContactKey, 101, cancellationToken: CancellationToken));
        await f.Storage.HistoryClear.ClearAsync(f.NodeId, f.ConversationId, CancellationToken);
        using (var connection = f.Open()) using (var command = connection.CreateCommand())
        { command.CommandText = "DELETE FROM Conversations; DELETE FROM Contacts;"; command.ExecuteNonQuery(); }
        await f.Storage.DisposeAsync();
        f.Storage = await MeshCoreMessenger.Core.Persistence.LocalStorage.OpenAsync(f.Paths, CancellationToken);
        var retained = await f.Storage.ContactDeliveries.GetPageAsync(f.NodeId, DeliveryContactKey, cancellationToken: CancellationToken);
        Assert.Equal(3, retained.Items.Count);
        Assert.All(retained.Items, item => { Assert.Null(item.MessageId); var evidence = Assert.Single(item.Evidence);
            Assert.NotNull(evidence.LearnedRoute); var candidate = Assert.Single(evidence.Candidates); Assert.Null(candidate.AttemptId); Assert.NotNull(candidate.ConfiguredRoute); });
        Assert.Empty((await f.Storage.ContactDeliveries.GetPageAsync(Guid.NewGuid(), DeliveryContactKey, cancellationToken: CancellationToken)).Items);
    }

    [Fact]
    public async Task AdditionalAckHasItsOwnImmutableReadbackAndDoesNotReplaceFirstSuccess()
    {
        await using var f = await Fixture.CreateAsync();
        var message = await BeginCycle(f);
        for (var previous = 0; previous < 2; previous++)
        {
            var prepared = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(PrivatePreparation(f, message.MessageId, previous), CancellationToken);
            await Expire(f, prepared);
        }
        var first = await ConfirmForReadback(f);
        var second = await ConfirmForReadback(f, 2, PrivatePcTime.AddDays(-2));
        Assert.NotEqual(first.EvidenceId, second.EvidenceId);
        await f.Storage.OutgoingMessages.EnrichDeliveryRouteAsync(new(first, new(0, ReadOnlyMemory<byte>.Empty, PrivatePcTime)), CancellationToken);
        await f.Storage.OutgoingMessages.EnrichDeliveryRouteAsync(new(second, Route(false)), CancellationToken);
        var record = Assert.Single((await f.Storage.ContactDeliveries.GetPageAsync(f.NodeId, DeliveryContactKey, cancellationToken: CancellationToken)).Items);
        Assert.Equal(PrivatePcTime.ToUniversalTime(), record.FirstAckReceivedUtc);
        Assert.Equal(2, record.Evidence.Count);
        Assert.Equal(PrivateRouteKind.Direct, record.Evidence.Single(item => item.AckTag == 1).LearnedRoute!.Kind);
        Assert.Equal(PrivateRouteKind.Path, record.Evidence.Single(item => item.AckTag == 2).LearnedRoute!.Kind);
        var repeats = 0; f.Storage.OutgoingMessages.MessageCommitted += (_, commit) => { if (commit.RouteReadback is not null) repeats++; };
        await f.Storage.OutgoingMessages.ConfirmAcknowledgementAsync(new(f.NodeId, f.SessionId, 1, 5, PrivatePcTime), CancellationToken);
        Assert.Equal(0, repeats);
    }
    [Fact]
    public async Task AmbiguousSuccessExposesAllConfiguredCandidatesWithoutChoosingOne()
    {
        await using var f = await Fixture.CreateAsync();
        var message = await BeginCycle(f, flood: false);
        for (var previous = 0; previous < 2; previous++)
        {
            var preparation = PrivatePreparation(f, message.MessageId, previous, flood: false);
            if (previous == 1) preparation = preparation with { Route = new(0, ReadOnlyMemory<byte>.Empty, PrivatePcTime) };
            var prepared = await f.Storage.OutgoingMessages.PreparePrivateAttemptAsync(preparation, CancellationToken);
            await Expire(f, prepared);
        }
        f.Execute("UPDATE SendAttempts SET ExpectedAck=X'01000000';");
        var target = await ConfirmForReadback(f);
        await f.Storage.OutgoingMessages.EnrichDeliveryRouteAsync(new(target, Route(true)), CancellationToken);
        var record = Assert.Single((await f.Storage.ContactDeliveries.GetPageAsync(f.NodeId, DeliveryContactKey, cancellationToken: CancellationToken)).Items);
        Assert.Equal(DeliveryAttribution.MultipleCandidates, record.Attribution);
        var evidence = Assert.Single(record.Evidence);
        Assert.Equal(2, evidence.Candidates.Count);
        Assert.Equal(new[] {PrivateRouteKind.Path, PrivateRouteKind.Direct}, evidence.Candidates.Select(item => item.ConfiguredRoute!.Kind));
        Assert.Equal(PrivateRouteKind.Flood, evidence.LearnedRoute!.Kind);
    }

    [Fact]
    public async Task LegacySuccessDoesNotInventConfiguredRouteOrSendTime()
    {
        await using var f = await Fixture.CreateAsync();
        var message = await f.Storage.OutgoingMessages.PrepareAsync(f.Request(), CancellationToken);
        var transition = new OutgoingAttemptTransition(f.NodeId, message.MessageId, message.Attempt.Id, f.SessionId,
            SendAttemptState.Prepared, SendAttemptState.Sending, PrivatePcTime);
        await f.Storage.OutgoingMessages.TransitionAsync(transition, CancellationToken);
        await f.Storage.OutgoingMessages.TransitionAsync(transition with { ExpectedState = SendAttemptState.Sending,
            State = SendAttemptState.Accepted, AckExpectation = AckExpectation.Expected, ExpectedAck = new byte[]{1,0,0,0}, WireTimestamp = 1 }, CancellationToken);
        await ConfirmForReadback(f);
        var record = Assert.Single((await f.Storage.ContactDeliveries.GetPageAsync(f.NodeId, DeliveryContactKey, cancellationToken: CancellationToken)).Items);
        Assert.Equal(DeliveryAttribution.Unknown, record.Attribution);
        Assert.Empty(Assert.Single(record.Evidence).Candidates);
        Assert.Null(Assert.Single(record.Evidence).LearnedRoute);
    }

}
