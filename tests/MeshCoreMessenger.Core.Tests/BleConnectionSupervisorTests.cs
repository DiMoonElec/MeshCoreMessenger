using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Exceptions;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class ConnectionSupervisorTests
{
    public static IEnumerable<object[]> BleFailures => Enum.GetValues<MeshCoreBleFailureReason>().Select(reason =>
        new object[] { reason, reason is MeshCoreBleFailureReason.AdapterUnavailable or MeshCoreBleFailureReason.RadioOff
            or MeshCoreBleFailureReason.DeviceUnreachable or MeshCoreBleFailureReason.TransportFailure
            or MeshCoreBleFailureReason.WriteOutcomeUnknown
                ? ConnectionFailureDisposition.Transient : ConnectionFailureDisposition.NeedsAttention });

    [Theory]
    [MemberData(nameof(BleFailures))]
    public void BleReasonIsClassifiedBeforeGenericTransportFailure(MeshCoreBleFailureReason reason, ConnectionFailureDisposition expected)
    {
        var error = new MeshCoreBleTransportException(reason, "test");
        var classifier = new ConnectionFailureClassifier();
        Assert.Equal(expected, classifier.Classify(error));
        Assert.Equal(expected, classifier.Classify(new AggregateException(new AggregateException(error))));
        Assert.Equal(ConnectionFailureDisposition.Transient, classifier.Classify(new MeshCoreTransportException("TCP/Serial")));
        Assert.Equal(ConnectionFailureDisposition.NeedsAttention, classifier.Classify(new ArgumentException("invalid endpoint")));
    }

    [Theory]
    [InlineData(MeshCoreBleFailureReason.PairingRequired)]
    [InlineData(MeshCoreBleFailureReason.AccessDenied)]
    [InlineData(MeshCoreBleFailureReason.UnsupportedPlatform)]
    [InlineData(MeshCoreBleFailureReason.MissingService)]
    [InlineData(MeshCoreBleFailureReason.InsufficientMtu)]
    [InlineData(MeshCoreBleFailureReason.IncompleteNotification)]
    public async Task PermanentBleFailureDoesNotEnterBackgroundRetry(MeshCoreBleFailureReason reason)
    {
        var context = await CreateBleContextAsync();
        context.Factory.EnqueueStart((_, _) => throw new MeshCoreBleTransportException(reason, "explicit action required"));
        await using var supervisor = context.CreateSupervisor();
        await supervisor.ConnectNowAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.NeedsAttention);
        Assert.Empty(context.Delay.History);
        Assert.Equal(1, context.Factory.CreateCount);
        Assert.Equal(ConnectionTransportKind.Ble, supervisor.Snapshot.UsedProfile!.Transport);
    }

    [Theory]
    [InlineData(MeshCoreBleFailureReason.RadioOff)]
    [InlineData(MeshCoreBleFailureReason.AdapterUnavailable)]
    [InlineData(MeshCoreBleFailureReason.DeviceUnreachable)]
    [InlineData(MeshCoreBleFailureReason.WriteOutcomeUnknown)]
    public async Task TransientBleFailureReusesBackoffAndCreatesFreshGeneration(MeshCoreBleFailureReason reason)
    {
        var context = await CreateBleContextAsync();
        context.Factory.EnqueueStart((_, _) => throw new MeshCoreBleTransportException(reason, "temporarily unavailable"));
        await using var supervisor = context.CreateSupervisor();
        await supervisor.ConnectNowAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.RetryWaiting);
        await WaitUntilAsync(() => context.Delay.History.Count == 1);
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(context.Delay.History));
        context.Delay.Complete(0);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online, 2);
        Assert.Equal(2, context.Factory.CreateCount);
        Assert.Equal(1, context.Factory.MaxActiveCount);
        Assert.NotEqual(context.Factory.Attempts[0].SessionId, context.Factory.Attempts[1].SessionId);
    }

    [Fact]
    public async Task BleRadioOffHonorsReconnectFalseAndManualDisconnect()
    {
        var noRetry = await CreateBleContextAsync(reconnect: false);
        noRetry.Factory.EnqueueStart((_, _) => throw new MeshCoreBleTransportException(MeshCoreBleFailureReason.RadioOff, "off"));
        await using (var supervisor = noRetry.CreateSupervisor())
        {
            await supervisor.ConnectNowAsync(CancellationToken);
            await WaitUntilAsync(() => noRetry.Factory.Attempts.Count == 1 && noRetry.Factory.Attempts[0].Stopped);
            await WaitForStateAsync(supervisor, ConnectionSupervisorState.Offline);
            Assert.Empty(noRetry.Delay.History);
        }
        var retry = await CreateBleContextAsync();
        retry.Factory.EnqueueStart((_, _) => throw new MeshCoreBleTransportException(MeshCoreBleFailureReason.RadioOff, "off"));
        await using var second = retry.CreateSupervisor();
        await second.ConnectNowAsync(CancellationToken);
        await WaitForStateAsync(second, ConnectionSupervisorState.RetryWaiting);
        await WaitUntilAsync(() => retry.Delay.Requests.Count == 1);
        await second.DisconnectAsync(CancellationToken);
        Assert.Equal(ConnectionSupervisorState.Offline, second.Snapshot.State);
        Assert.True(retry.Delay.Requests[0].IsCanceled);
        Assert.Equal(1, retry.Factory.CreateCount);
    }

    private static async Task<SupervisorContext> CreateBleContextAsync(bool reconnect = true)
    {
        var context = CreateContext(reconnect: reconnect);
        var profile = context.Profiles.Profiles[0] with
        {
            Transport = ConnectionTransportKind.Ble, BleDeviceId = "offline-device", TcpHost = null, TcpPort = null,
        };
        context.Profiles.Profiles[0] = profile;
        await context.Profiles.SelectAsync(profile.Id, CancellationToken);
        return context;
    }
}
