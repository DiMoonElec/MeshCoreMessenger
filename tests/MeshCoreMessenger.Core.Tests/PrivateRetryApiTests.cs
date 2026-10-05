using System.Buffers.Binary;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp;
using MeshCoreSharp.Models;
using MeshCoreSharp.Transport.Tcp;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class PrivateRetryApiTests
{
    [Theory]
    [InlineData(true, PrivateRepeatMode.SameTimestampIncrementAttempt)]
    [InlineData(false, PrivateRepeatMode.SameTimestampIncrementAttempt)]
    [InlineData(true, PrivateRepeatMode.NewTimestampResetAttempt)]
    [InlineData(false, PrivateRepeatMode.NewTimestampResetAttempt)]
    public void PlanKeepsLocalOrdinalSeparateFromWireIdentityAndResetsOnlyAtFallback(
        bool initialFlood, PrivateRepeatMode mode)
    {
        var policy = new PrivateRetryPolicy(mode);
        var plan = PrivateRetryPlan.Create(initialFlood, policy);
        var expectedCount = initialFlood ? 3 : 5;
        Assert.Equal(expectedCount, plan.Steps.Count);
        Assert.Equal(mode, plan.Policy.RetryMode);
        Assert.Equal(Enumerable.Range(1, expectedCount), plan.Steps.Select(step => step.AttemptNumber));
        var newOnEverySend = mode == PrivateRepeatMode.NewTimestampResetAttempt;
        Assert.Equal(newOnEverySend ? new byte[expectedCount] : initialFlood ? [0, 1, 2] : [0, 1, 2, 0, 1],
            plan.Steps.Select(step => step.WireAttempt));
        Assert.Equal(newOnEverySend ? Enumerable.Range(1, expectedCount) : initialFlood ? [1, 1, 1] : [1, 1, 1, 2, 2],
            plan.Steps.Select(step => step.WireMessageOrdinal));
        Assert.All(plan.Steps, step => Assert.Equal(!initialFlood && step.AttemptNumber == 4, step.ResetRouteBeforeSend));
        Assert.All(plan.Steps, step => Assert.Equal(initialFlood ? PrivateDeliveryPhase.Flood
            : step.AttemptNumber <= 3 ? PrivateDeliveryPhase.KnownRoute : PrivateDeliveryPhase.FallbackFlood, step.Phase));
        Assert.All(plan.Steps, step => Assert.Equal(newOnEverySend || step.AttemptNumber == 1 || step.ResetRouteBeforeSend,
            step.RequiresNewTimestamp));
    }

    [Fact]
    public void TimestampResolutionRequiresDurableReservationWithoutReadingTheClock()
    {
        var plan = PrivateRetryPlan.Create(false);
        Assert.Equal(PrivateRepeatMode.SameTimestampIncrementAttempt, plan.Policy.RetryMode);
        Assert.Throws<ArgumentException>(() => plan.Steps[0].ResolveTimestamp(null));
        Assert.Equal(100u, plan.Steps[0].ResolveTimestamp(null, 100));
        Assert.Equal(100u, plan.Steps[1].ResolveTimestamp(100));
        Assert.Throws<ArgumentException>(() => plan.Steps[1].ResolveTimestamp(null));
        Assert.Throws<ArgumentException>(() => plan.Steps[1].ResolveTimestamp(100, 101));
        Assert.Throws<ArgumentException>(() => plan.Steps[3].ResolveTimestamp(null, 101));
        Assert.Throws<ArgumentException>(() => plan.Steps[3].ResolveTimestamp(100));
        Assert.Throws<ArgumentException>(() => plan.Steps[3].ResolveTimestamp(100, 100));
        Assert.Throws<ArgumentException>(() => plan.Steps[3].ResolveTimestamp(100, 99));
        Assert.Equal(101u, plan.Steps[3].ResolveTimestamp(100, 101));
        Assert.Equal(uint.MaxValue, plan.Steps[4].ResolveTimestamp(uint.MaxValue));
        Assert.Throws<ArgumentException>(() => plan.Steps[3].ResolveTimestamp(uint.MaxValue, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrivateRetryPolicy((PrivateRepeatMode)999));
    }

    [Theory]
    [InlineData(true, PrivateRepeatMode.SameTimestampIncrementAttempt)]
    [InlineData(false, PrivateRepeatMode.SameTimestampIncrementAttempt)]
    [InlineData(true, PrivateRepeatMode.NewTimestampResetAttempt)]
    [InlineData(false, PrivateRepeatMode.NewTimestampResetAttempt)]
    public async Task TcpAdapterSendsPlannedWireIdentitiesAndMatchesFirmwareAcksInReverseOrder(
        bool initialFlood, PrivateRepeatMode mode)
    {
        await using var server = new FakeCompanionServer
        {
            AutoAcknowledgePrivate = false, UseProtocolPrivateAckTags = true,
            PrivateSuggestedTimeoutMilliseconds = 5000,
        };
        server.ContactRoutes[0xA1] = initialFlood ? byte.MaxValue : (byte)0;
        server.Start();
        await using var client = new MeshCoreClientAdapter(new MeshCoreClient(
            new TcpMeshCoreTransport("127.0.0.1", server.Port),
            new MeshCoreClientOptions { AutoReceiveMessages = false, CommandTimeout = TimeSpan.FromSeconds(2),
                MinimumAckTimeout = TimeSpan.FromSeconds(5), MaximumAckTimeout = TimeSpan.FromSeconds(5), AckTimeoutMargin = TimeSpan.Zero }));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = stop.Token;
        await client.ConnectAsync(token);
        await client.StartAsync(token);
        var key = Enumerable.Repeat((byte)0xA1, 32).ToArray();
        var plan = PrivateRetryPlan.Create(initialFlood, new(mode));
        var sends = new List<TextMessageSendResult>();
        uint? previousTimestamp = null;
        foreach (var step in plan.Steps)
        {
            // Explicit test driver, not an automatic coordinator. Each call sends exactly once.
            if (step.ResetRouteBeforeSend) await client.ResetPathAsync(key, token);
            var timestamp = step.ResolveTimestamp(previousTimestamp,
                step.RequiresNewTimestamp ? previousTimestamp is { } previous ? previous + 1 : 1_700_000_123u : null);
            var sent = await client.SendTextAsync(key, "Тест 👋", timestamp, step.WireAttempt, token);
            sends.Add(sent);
            previousTimestamp = timestamp;
            Assert.Equal(timestamp, sent.Timestamp);
            Assert.Equal(initialFlood || step.AttemptNumber > 3, sent.Accepted.IsFlood);
        }
        var transmissions = server.PrivateTransmissions.ToArray();
        Assert.Equal(plan.Steps.Count, transmissions.Length);
        Assert.Equal(plan.Steps.Select(step => step.WireAttempt), transmissions.Select(tx => tx.Attempt));
        Assert.Equal(sends.Select(sent => sent.Timestamp), transmissions.Select(tx => tx.Timestamp));
        Assert.Equal(transmissions.Length, transmissions.Select(tx => tx.ExpectedAck).Distinct().Count());
        Assert.All(transmissions, tx => Assert.Equal("Тест 👋", tx.Text));
        Assert.Equal(initialFlood ? 0 : 1, server.RouteResets.Count);
        if (!initialFlood && mode == PrivateRepeatMode.SameTimestampIncrementAttempt)
        {
            // Independent fixture computed from timestamp | attempt | UTF-8 text | key 00..1F.
            string[] tags = ["908A06F6", "9AAB264B", "159A6C30", "73BCB2E7", "3FCB2794"];
            Assert.Equal(tags.Select(tag => BinaryPrimitives.ReadUInt32LittleEndian(Convert.FromHexString(tag))),
                transmissions.Select(tx => tx.ExpectedAck));
        }

        await server.SendAcknowledgementAsync(0xDEADBEEF);
        await client.FlushEventsAsync(token);
        Assert.All(sends, sent => Assert.False(sent.Delivery.IsCompleted));
        for (var index = sends.Count - 1; index >= 0; index--)
        {
            var tag = transmissions[index].ExpectedAck;
            await server.SendAcknowledgementAsync(tag, (uint)(100 + index));
            var result = await sends[index].Delivery.WaitAsync(token);
            Assert.Equal(MessageDeliveryStatus.Confirmed, result.Status);
            Assert.Equal(tag, result.Acknowledgement!.Ack);
            Assert.Equal((uint)(100 + index), result.Acknowledgement.RoundTripTimeMilliseconds);
            await server.SendAcknowledgementAsync(tag); // A duplicate must not satisfy another waiter.
            Assert.All(sends.Take(index), sent => Assert.False(sent.Delivery.IsCompleted));
        }
        Assert.Equal(plan.Steps.Count, server.PrivateTransmissions.Count);
        await client.DisconnectAsync(token);
        await server.Completion.WaitAsync(token);
    }
}
