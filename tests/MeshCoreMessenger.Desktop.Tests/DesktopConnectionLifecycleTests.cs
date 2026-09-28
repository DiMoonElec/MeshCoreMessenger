using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.Lifecycle;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class DesktopConnectionLifecycleTests
{
    [Fact]
    public async Task RepeatedStartupAndShutdownInvokeSupervisorOnce()
    {
        var supervisor = new FakeSupervisor();
        await using var lifecycle = new DesktopConnectionLifecycle(supervisor);

        await Task.WhenAll(
            lifecycle.StartAsync(CancellationToken),
            lifecycle.StartAsync(CancellationToken));
        await Task.WhenAll(
            lifecycle.ShutdownAsync(CancellationToken),
            lifecycle.ShutdownAsync(CancellationToken));

        Assert.Equal(1, supervisor.StartCount);
        Assert.Equal(1, supervisor.ShutdownCount);
    }

    [Fact]
    public async Task ShutdownWaitsForAcceptedStartupBeforeStoppingSupervisor()
    {
        var startupGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var supervisor = new FakeSupervisor { StartupGate = startupGate };
        await using var lifecycle = new DesktopConnectionLifecycle(supervisor);

        var startup = lifecycle.StartAsync(CancellationToken);
        var shutdown = lifecycle.ShutdownAsync(CancellationToken);
        await Task.Yield();

        Assert.Equal(0, supervisor.ShutdownCount);
        startupGate.SetResult();
        await Task.WhenAll(startup, shutdown);

        Assert.Equal(1, supervisor.StartCount);
        Assert.Equal(1, supervisor.ShutdownCount);
    }

    [Fact]
    public async Task StartupAfterShutdownIsRejected()
    {
        var supervisor = new FakeSupervisor();
        await using var lifecycle = new DesktopConnectionLifecycle(supervisor);
        await lifecycle.ShutdownAsync(CancellationToken);

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            lifecycle.StartAsync(CancellationToken));
    }

    [Fact]
    public async Task StartupCommandDoesNotWaitForBlockedConnectionCreation()
    {
        var profile = CreateProfile(autoConnect: true);
        var attempts = new BlockingAttemptFactory();
        await using var supervisor = new ConnectionSupervisor(
            new FakeProfileManager(profile),
            attempts,
            new ConnectionFailureClassifier(),
            new SystemReconnectDelay(),
            new ZeroJitter(),
            TimeProvider.System);
        await using var lifecycle = new DesktopConnectionLifecycle(supervisor);

        await lifecycle.StartAsync(CancellationToken);
        await attempts.CreationStarted.Task.WaitAsync(CancellationToken);

        Assert.Equal(ConnectionSupervisorState.Connecting, supervisor.Snapshot.State);
        Assert.Equal(1, attempts.CreateCount);
    }

    [Fact]
    public async Task AutoConnectDisabledDoesNotCreateTransportAttempt()
    {
        var profile = CreateProfile(autoConnect: false);
        var attempts = new BlockingAttemptFactory();
        await using var supervisor = new ConnectionSupervisor(
            new FakeProfileManager(profile),
            attempts,
            new ConnectionFailureClassifier(),
            new SystemReconnectDelay(),
            new ZeroJitter(),
            TimeProvider.System);
        await using var lifecycle = new DesktopConnectionLifecycle(supervisor);

        await lifecycle.StartAsync(CancellationToken);

        Assert.Equal(ConnectionSupervisorState.Offline, supervisor.Snapshot.State);
        Assert.Equal(0, attempts.CreateCount);
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static ConnectionProfile CreateProfile(bool autoConnect) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test",
        Transport = ConnectionTransportKind.Tcp,
        TcpHost = "127.0.0.1",
        TcpPort = 5000,
        AutoConnect = autoConnect,
        Reconnect = true,
        CreatedUtc = DateTimeOffset.UnixEpoch,
        UpdatedUtc = DateTimeOffset.UnixEpoch,
    };

    private sealed class FakeProfileManager(ConnectionProfile profile) : IConnectionProfileManager
    {
        public Task<IReadOnlyList<ConnectionProfile>> GetProfilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>([profile]);

        public Task<ConnectionProfile?> GetSelectedProfileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ConnectionProfile?>(profile);

        public Task<ConnectionProfile> SelectAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(profile);

        public Task<ConnectionProfile> SaveAndSelectAsync(
            ConnectionProfileDraft draft,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ConnectionProfile> UpdateExpectedNodePublicKeyAsync(
            Guid profileId,
            ReadOnlyMemory<byte> publicKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingAttemptFactory : IConnectionAttemptFactory
    {
        public TaskCompletionSource CreationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CreateCount { get; private set; }

        public async Task<IConnectionAttempt> CreateAsync(
            ConnectionProfile profile,
            long generation,
            CancellationToken cancellationToken = default)
        {
            CreateCount++;
            CreationStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocked factory unexpectedly completed.");
        }
    }

    private sealed class ZeroJitter : IReconnectJitter
    {
        public double GetJitterFraction(int retryNumber) => 0;
    }

    private sealed class FakeSupervisor : IConnectionSupervisor
    {
        public int StartCount { get; private set; }
        public int ShutdownCount { get; private set; }
        public TaskCompletionSource? StartupGate { get; init; }
        public ConnectionSupervisorSnapshot Snapshot { get; } = new(
            ConnectionSupervisorState.Offline,
            0,
            null,
            null,
            null,
            null,
            null);

        public event EventHandler<ConnectionSupervisorStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public async Task StartAutoConnectAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            if (StartupGate is not null)
            {
                await StartupGate.Task.WaitAsync(cancellationToken);
            }
        }

        public Task ConnectNowAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SwitchProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            ShutdownCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
