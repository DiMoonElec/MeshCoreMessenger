using System.Collections.Concurrent;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Exceptions;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class ConnectionSupervisorTests
{
    [Fact]
    public async Task HappyPathPublishesExactStateSequence()
    {
        var context = CreateContext();
        await using var supervisor = context.CreateSupervisor();
        var states = new ConcurrentQueue<ConnectionSupervisorState>();
        supervisor.StateChanged += (_, args) => states.Enqueue(args.Current.State);

        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online);
        await supervisor.DisconnectAsync(CancellationToken);

        Assert.Equal(
            [
                ConnectionSupervisorState.Connecting,
                ConnectionSupervisorState.Identifying,
                ConnectionSupervisorState.Synchronizing,
                ConnectionSupervisorState.Online,
                ConnectionSupervisorState.Disconnecting,
                ConnectionSupervisorState.Offline,
            ],
            states);
        Assert.Equal(1, context.Factory.MaxActiveCount);
    }

    [Fact]
    public async Task TransientFailureWaitsThenCreatesFreshAttempt()
    {
        var context = CreateContext();
        context.Factory.EnqueueStart((_, _) => throw new MeshCoreTransportException("unplugged"));
        await using var supervisor = context.CreateSupervisor();

        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.RetryWaiting);
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(context.Delay.History));
        Assert.True(context.Factory.Attempts[0].Stopped);

        context.Delay.Complete(0);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online);

        Assert.Equal(2, context.Factory.CreateCount);
        Assert.Equal(2, supervisor.Snapshot.Generation);
        Assert.Equal(1, context.Factory.MaxActiveCount);
        Assert.NotEqual(context.Factory.Attempts[0].SessionId, context.Factory.Attempts[1].SessionId);
    }

    [Fact]
    public async Task PermanentFailureNeedsAttentionWithoutRetry()
    {
        var context = CreateContext();
        context.Factory.EnqueueStart((_, _) => throw new NodeIdentityMismatchException());
        await using var supervisor = context.CreateSupervisor();

        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.NeedsAttention);

        Assert.Empty(context.Delay.History);
        Assert.Equal(1, context.Factory.CreateCount);
        Assert.False(context.Factory.Attempts[0].Stopped);
    }

    [Fact]
    public async Task RepeatedConnectCommandsNeverCreateParallelAttempts()
    {
        var context = CreateContext();
        context.Factory.EnqueueStart(async (_, cancellationToken) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        await using var supervisor = context.CreateSupervisor();

        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Connecting);
        await Task.WhenAll(
            supervisor.ConnectNowAsync(CancellationToken),
            supervisor.ConnectNowAsync(CancellationToken),
            supervisor.ConnectNowAsync(CancellationToken));

        Assert.Equal(1, context.Factory.CreateCount);
        Assert.Equal(1, context.Factory.MaxActiveCount);
        await supervisor.DisconnectAsync(CancellationToken);
    }

    [Fact]
    public async Task ConnectNowInterruptsRetryWithoutStartingSecondLoop()
    {
        var context = CreateContext();
        context.Factory.EnqueueStart((_, _) => throw new MeshCoreTransportException("offline"));
        await using var supervisor = context.CreateSupervisor();

        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.RetryWaiting);
        await supervisor.ConnectNowAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online);
        await supervisor.ConnectNowAsync(CancellationToken);

        Assert.True(context.Delay.Requests[0].IsCanceled);
        Assert.Equal(2, context.Factory.CreateCount);
        Assert.Equal(1, context.Factory.MaxActiveCount);
    }

    [Fact]
    public async Task ManualDisconnectCancelsRetryAndSuppressesNewAttempt()
    {
        var context = CreateContext();
        context.Factory.EnqueueStart((_, _) => throw new MeshCoreTransportException("offline"));
        await using var supervisor = context.CreateSupervisor();

        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.RetryWaiting);
        await supervisor.DisconnectAsync(CancellationToken);
        await Task.Yield();

        Assert.Equal(ConnectionSupervisorState.Offline, supervisor.Snapshot.State);
        Assert.True(context.Delay.Requests[0].IsCanceled);
        Assert.Equal(1, context.Factory.CreateCount);
    }

    [Fact]
    public async Task ReconnectDisabledReturnsOfflineWithoutDelay()
    {
        var context = CreateContext(reconnect: false);
        context.Factory.EnqueueStart((_, _) => throw new MeshCoreTransportException("offline"));
        await using var supervisor = context.CreateSupervisor();

        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Offline, generation: 1);

        Assert.Empty(context.Delay.History);
        Assert.Equal(1, context.Factory.CreateCount);
    }

    [Fact]
    public async Task AutoConnectFlagOnlyControlsStartupAndManualConnectStillWorks()
    {
        var context = CreateContext(autoConnect: false);
        await using var supervisor = context.CreateSupervisor();

        await supervisor.StartAutoConnectAsync(CancellationToken);
        Assert.Equal(ConnectionSupervisorState.Offline, supervisor.Snapshot.State);
        Assert.Equal(0, context.Factory.CreateCount);

        await supervisor.ConnectNowAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online);
        Assert.Equal(1, context.Factory.CreateCount);
    }

    [Fact]
    public async Task BackoffAndJitterAreDeterministicAndCapped()
    {
        var context = CreateContext(jitter: [0, 0.2, -0.2, 0, 0, 0, 0]);
        for (var index = 0; index < 7; index++)
        {
            context.Factory.EnqueueStart((_, _) => throw new MeshCoreTransportException("offline"));
        }
        await using var supervisor = context.CreateSupervisor();
        var expected = new[] { 1d, 2.4, 3.2, 8d, 15d, 30d, 30d };

        await supervisor.StartAutoConnectAsync(CancellationToken);
        for (var index = 0; index < expected.Length; index++)
        {
            await WaitUntilAsync(() => context.Delay.History.Count > index);
            Assert.Equal(expected[index], context.Delay.History[index].TotalSeconds, precision: 6);
            if (index + 1 < expected.Length)
            {
                context.Delay.Complete(index);
            }
        }
    }

    [Fact]
    public async Task StableOnlinePeriodResetsBackoff()
    {
        var context = CreateContext();
        context.Factory.EnqueueStart((_, _) => throw new MeshCoreTransportException("first"));
        await using var supervisor = context.CreateSupervisor();

        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.RetryWaiting);
        context.Delay.Complete(0);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online);

        context.Time.Advance(TimeSpan.FromSeconds(60));
        context.Factory.Attempts[1].Fail(new MeshCoreTransportException("later"));
        await WaitUntilAsync(() => context.Delay.History.Count == 2);

        Assert.Equal(TimeSpan.FromSeconds(1), context.Delay.History[1]);
    }

    [Fact]
    public async Task LateProgressFromOldGenerationCannotChangeCurrentState()
    {
        var context = CreateContext();
        context.Factory.EnqueueStart((_, _) => throw new MeshCoreTransportException("first"));
        await using var supervisor = context.CreateSupervisor();

        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.RetryWaiting);
        var oldAttempt = context.Factory.Attempts[0];
        context.Delay.Complete(0);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online);

        oldAttempt.EmitProgress(ConnectionAttemptPhase.Identifying);
        await Task.Delay(20, CancellationToken);

        Assert.Equal(ConnectionSupervisorState.Online, supervisor.Snapshot.State);
        Assert.Equal(2, supervisor.Snapshot.Generation);
    }

    [Theory]
    [InlineData(ConnectionSupervisorState.Connecting)]
    [InlineData(ConnectionSupervisorState.Synchronizing)]
    [InlineData(ConnectionSupervisorState.Online)]
    [InlineData(ConnectionSupervisorState.RetryWaiting)]
    public async Task ShutdownCompletesFromEveryActiveState(ConnectionSupervisorState state)
    {
        var context = CreateContext();
        if (state == ConnectionSupervisorState.Connecting)
        {
            context.Factory.EnqueueStart(async (_, cancellationToken) =>
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        }
        else if (state == ConnectionSupervisorState.Synchronizing)
        {
            context.Factory.EnqueueStart(async (attempt, cancellationToken) =>
            {
                attempt.EmitProgress(ConnectionAttemptPhase.Identifying);
                attempt.EmitProgress(ConnectionAttemptPhase.Synchronizing);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        }
        else if (state == ConnectionSupervisorState.RetryWaiting)
        {
            context.Factory.EnqueueStart((_, _) => throw new MeshCoreTransportException("offline"));
        }

        var supervisor = context.CreateSupervisor();
        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, state);

        await supervisor.ShutdownAsync(CancellationToken);

        Assert.Equal(ConnectionSupervisorState.Offline, supervisor.Snapshot.State);
        Assert.All(context.Factory.Attempts, attempt => Assert.True(attempt.Stopped));
        Assert.Equal(0, context.Factory.ActiveCount);
    }

    [Fact]
    public async Task ReceiveTimeoutClosesOldSessionBeforeRetryingWithNewOne()
    {
        var context = CreateContext();
        context.Factory.EnqueueStart((_, _) => throw new ReceiveDrainTimeoutException(
            "SYNC_NEXT_MESSAGE timed out", new TimeoutException()));
        await using var supervisor = context.CreateSupervisor();

        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.RetryWaiting);
        Assert.True(context.Factory.Attempts[0].Stopped);
        context.Delay.Complete(0);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online);

        Assert.Equal(2, context.Factory.CreateCount);
        Assert.Equal(1, context.Factory.MaxActiveCount);
    }

    [Fact]
    public async Task DatabaseAndIngestFailuresNeverStartReconnectLoop()
    {
        foreach (var exception in new Exception[]
                 {
                     new DatabaseStorageException("database read-only"),
                     new ReceiveIngestException(new IOException("disk full")),
                 })
        {
            var context = CreateContext();
            context.Factory.EnqueueStart((_, _) => throw exception);
            await using var supervisor = context.CreateSupervisor();

            await supervisor.StartAutoConnectAsync(CancellationToken);
            await WaitForStateAsync(supervisor, ConnectionSupervisorState.NeedsAttention);

            Assert.Empty(context.Delay.History);
            Assert.Equal(1, context.Factory.CreateCount);
        }
    }

    [Fact]
    public async Task PersistenceFailureDuringTeardownOverridesTransientReconnect()
    {
        var context = CreateContext();
        await using var supervisor = context.CreateSupervisor();
        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online);
        var attempt = context.Factory.Attempts[0];
        attempt.StopError = new ConnectionAttemptPersistenceException(
            "ingest barrier failed", new IOException("disk full"));

        attempt.Fail(new MeshCoreTransportException("unplugged"));
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.NeedsAttention);

        Assert.Empty(context.Delay.History);
        Assert.Equal(1, context.Factory.CreateCount);
    }

    [Fact]
    public async Task SwitchProfileFullyStopsOldAttemptBeforeCreatingNewOne()
    {
        var context = CreateContext();
        var second = CreateProfile("Second");
        context.Profiles.Profiles.Add(second);
        await using var supervisor = context.CreateSupervisor();
        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online);
        var oldAttempt = context.Factory.Attempts[0];

        await supervisor.SwitchProfileAsync(second.Id, CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online, generation: 2);

        Assert.True(oldAttempt.Stopped);
        Assert.Equal([context.Profiles.Profiles[0].Id, second.Id], context.Factory.Attempts.Select(item => item.ProfileId));
        Assert.Equal(second.Id, supervisor.Snapshot.ProfileId);
        Assert.Equal(1, context.Factory.MaxActiveCount);
    }

    [Fact]
    public async Task SwitchProfileDoesNotCreateReplacementUntilOldStopCompletes()
    {
        var context = CreateContext();
        var second = CreateProfile("Second");
        context.Profiles.Profiles.Add(second);
        await using var supervisor = context.CreateSupervisor();
        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online);
        var oldAttempt = context.Factory.Attempts[0];
        oldAttempt.StopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await supervisor.SwitchProfileAsync(second.Id, CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Disconnecting);
        Assert.Equal(1, context.Factory.CreateCount);
        Assert.Equal(1, context.Factory.ActiveCount);

        oldAttempt.StopGate.SetResult();
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online, generation: 2);

        Assert.Equal(2, context.Factory.CreateCount);
        Assert.True(oldAttempt.Stopped);
        Assert.Equal(1, context.Factory.MaxActiveCount);
    }

    [Fact]
    public async Task SwitchProfileCancelsRetryAndStartsSelectedProfileImmediately()
    {
        var context = CreateContext();
        var second = CreateProfile("Second");
        context.Profiles.Profiles.Add(second);
        context.Factory.EnqueueStart((_, _) => throw new MeshCoreTransportException("offline"));
        await using var supervisor = context.CreateSupervisor();
        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.RetryWaiting);

        await supervisor.SwitchProfileAsync(second.Id, CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online, generation: 2);

        Assert.True(context.Delay.Requests[0].IsCanceled);
        Assert.Equal(second.Id, context.Factory.Attempts[1].ProfileId);
        Assert.Equal(1, context.Factory.MaxActiveCount);
    }

    [Fact]
    public async Task SuspendAndWakeReplaceOnlineAttemptWithFreshGeneration()
    {
        var power = new FakePowerEvents();
        var context = CreateContext(powerEvents: power);
        await using var supervisor = context.CreateSupervisor();
        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online);
        var oldAttempt = context.Factory.Attempts[0];

        power.Suspend();
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Offline);
        Assert.True(oldAttempt.Stopped);
        power.Resume();
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online, generation: 2);

        Assert.NotEqual(oldAttempt.SessionId, context.Factory.Attempts[1].SessionId);
        Assert.Equal(1, context.Factory.MaxActiveCount);
    }

    [Theory]
    [InlineData(ConnectionSupervisorState.Connecting)]
    [InlineData(ConnectionSupervisorState.Synchronizing)]
    public async Task SuspendAndWakeReplaceAttemptDuringStartup(ConnectionSupervisorState state)
    {
        var power = new FakePowerEvents();
        var context = CreateContext(powerEvents: power);
        context.Factory.EnqueueStart(async (attempt, cancellationToken) =>
        {
            if (state == ConnectionSupervisorState.Synchronizing)
            {
                attempt.EmitProgress(ConnectionAttemptPhase.Identifying);
                attempt.EmitProgress(ConnectionAttemptPhase.Synchronizing);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var supervisor = context.CreateSupervisor();
        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, state);

        power.Suspend();
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Offline);
        power.Resume();
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online, generation: 2);

        Assert.True(context.Factory.Attempts[0].Stopped);
        Assert.Equal(1, context.Factory.MaxActiveCount);
    }

    [Fact]
    public async Task SuspendCancelsRetryAndWakeCreatesExactlyOneAttempt()
    {
        var power = new FakePowerEvents();
        var context = CreateContext(powerEvents: power);
        context.Factory.EnqueueStart((_, _) => throw new MeshCoreTransportException("offline"));
        await using var supervisor = context.CreateSupervisor();
        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.RetryWaiting);

        power.Suspend();
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Offline);
        power.Resume();
        power.Resume();
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online, generation: 2);

        Assert.True(context.Delay.Requests[0].IsCanceled);
        Assert.Equal(2, context.Factory.CreateCount);
        Assert.Equal(1, context.Factory.MaxActiveCount);
    }

    [Fact]
    public async Task ManualDisconnectStaysOfflineAcrossSuspendAndWake()
    {
        var power = new FakePowerEvents();
        var context = CreateContext(powerEvents: power);
        await using var supervisor = context.CreateSupervisor();
        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online);
        await supervisor.DisconnectAsync(CancellationToken);

        power.Suspend();
        power.Resume();
        await Task.Delay(20, CancellationToken);

        Assert.Equal(ConnectionSupervisorState.Offline, supervisor.Snapshot.State);
        Assert.Equal(1, context.Factory.CreateCount);
    }

    [Fact]
    public async Task NeedsAttentionIsPreservedAcrossSuspendAndWakeWithoutReconnect()
    {
        var power = new FakePowerEvents();
        var context = CreateContext(powerEvents: power);
        context.Factory.EnqueueStart((_, _) => throw new ReceiveIngestException(new IOException("disk full")));
        await using var supervisor = context.CreateSupervisor();
        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.NeedsAttention);

        power.Suspend();
        await WaitUntilAsync(() => context.Factory.Attempts[0].Stopped);
        power.Resume();
        await Task.Delay(20, CancellationToken);

        Assert.Equal(ConnectionSupervisorState.NeedsAttention, supervisor.Snapshot.State);
        Assert.Equal(1, context.Factory.CreateCount);
        Assert.Empty(context.Delay.History);
    }

    [Fact]
    public async Task SwitchWhileSuspendedUsesSelectedProfileOnWake()
    {
        var power = new FakePowerEvents();
        var context = CreateContext(powerEvents: power);
        var second = CreateProfile("Second");
        context.Profiles.Profiles.Add(second);
        await using var supervisor = context.CreateSupervisor();
        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online);

        power.Suspend();
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Offline);
        await supervisor.SwitchProfileAsync(second.Id, CancellationToken);
        Assert.Equal(1, context.Factory.CreateCount);
        power.Resume();
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online, generation: 2);

        Assert.Equal(second.Id, context.Factory.Attempts[1].ProfileId);
    }

    [Fact]
    public async Task LateCallbackFromProfileBeforeSwitchCannotChangeNewGeneration()
    {
        var context = CreateContext();
        var second = CreateProfile("Second");
        context.Profiles.Profiles.Add(second);
        await using var supervisor = context.CreateSupervisor();
        await supervisor.StartAutoConnectAsync(CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online);
        var oldAttempt = context.Factory.Attempts[0];
        await supervisor.SwitchProfileAsync(second.Id, CancellationToken);
        await WaitForStateAsync(supervisor, ConnectionSupervisorState.Online, generation: 2);

        oldAttempt.EmitProgress(ConnectionAttemptPhase.Synchronizing);
        oldAttempt.Fail(new MeshCoreTransportException("late"));
        await Task.Delay(20, CancellationToken);

        Assert.Equal(ConnectionSupervisorState.Online, supervisor.Snapshot.State);
        Assert.Equal(2, supervisor.Snapshot.Generation);
        Assert.Equal(second.Id, supervisor.Snapshot.ProfileId);
    }

    [Fact]
    public async Task ShutdownUnsubscribesFromPlatformPowerEvents()
    {
        var power = new FakePowerEvents();
        var context = CreateContext(powerEvents: power);
        var supervisor = context.CreateSupervisor();
        Assert.Equal(2, power.SubscriptionCount);

        await supervisor.ShutdownAsync(CancellationToken);

        Assert.Equal(0, power.SubscriptionCount);
    }

    private static SupervisorContext CreateContext(
        bool reconnect = true,
        bool autoConnect = true,
        IReadOnlyList<double>? jitter = null,
        IPlatformPowerEvents? powerEvents = null)
    {
        var profile = CreateProfile("Test") with { AutoConnect = autoConnect, Reconnect = reconnect };
        return new SupervisorContext(
            new FakeProfileManager(profile),
            new FakeAttemptFactory(),
            new FakeReconnectDelay(),
            new FakeJitter(jitter ?? [0]),
            new FakeTimeProvider(DateTimeOffset.Parse("2026-09-28T00:00:00Z")),
            powerEvents);
    }

    private static ConnectionProfile CreateProfile(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Transport = ConnectionTransportKind.Tcp,
        TcpHost = "127.0.0.1",
        TcpPort = 5000,
        AutoConnect = true,
        Reconnect = true,
        CreatedUtc = DateTimeOffset.UnixEpoch,
        UpdatedUtc = DateTimeOffset.UnixEpoch,
    };

    private static async Task WaitForStateAsync(
        ConnectionSupervisor supervisor,
        ConnectionSupervisorState state,
        long? generation = null)
    {
        await WaitUntilAsync(() =>
            supervisor.Snapshot.State == state &&
            (generation is null || supervisor.Snapshot.Generation == generation));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected supervisor condition was not reached.");
            }
            await Task.Delay(5, CancellationToken);
        }
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed record SupervisorContext(
        FakeProfileManager Profiles,
        FakeAttemptFactory Factory,
        FakeReconnectDelay Delay,
        FakeJitter Jitter,
        FakeTimeProvider Time,
        IPlatformPowerEvents? PowerEvents)
    {
        public ConnectionSupervisor CreateSupervisor() => new(
            Profiles,
            Factory,
            new ConnectionFailureClassifier(),
            Delay,
            Jitter,
            Time,
            PowerEvents);
    }

    private sealed class FakeProfileManager(ConnectionProfile profile) : IConnectionProfileManager
    {
        private ConnectionProfile _selected = profile;
        public List<ConnectionProfile> Profiles { get; } = [profile];
        public Task<IReadOnlyList<ConnectionProfile>> GetProfilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>(Profiles);
        public Task<ConnectionProfile?> GetSelectedProfileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ConnectionProfile?>(_selected);
        public Task<ConnectionProfile> SelectAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _selected = Profiles.Single(item => item.Id == profileId);
            return Task.FromResult(_selected);
        }
        public Task<ConnectionProfile> SaveAndSelectAsync(ConnectionProfileDraft draft, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ConnectionProfile> UpdateExpectedNodePublicKeyAsync(Guid profileId, ReadOnlyMemory<byte> publicKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAttemptFactory : IConnectionAttemptFactory
    {
        private readonly Queue<Func<FakeAttempt, CancellationToken, Task>> _starts = [];
        private int _activeCount;
        public List<FakeAttempt> Attempts { get; } = [];
        public int CreateCount => Attempts.Count;
        public int ActiveCount => Volatile.Read(ref _activeCount);
        public int MaxActiveCount { get; private set; }

        public void EnqueueStart(Func<FakeAttempt, CancellationToken, Task> start) => _starts.Enqueue(start);

        public Task<IConnectionAttempt> CreateAsync(ConnectionProfile profile, long generation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = _starts.Count > 0 ? _starts.Dequeue() : DefaultStart;
            var attempt = new FakeAttempt(profile.Id, generation, start, OnStopped);
            Attempts.Add(attempt);
            var active = Interlocked.Increment(ref _activeCount);
            MaxActiveCount = Math.Max(MaxActiveCount, active);
            return Task.FromResult<IConnectionAttempt>(attempt);
        }

        private static Task DefaultStart(FakeAttempt attempt, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt.EmitProgress(ConnectionAttemptPhase.Identifying);
            attempt.NodeIdValue = Guid.NewGuid();
            attempt.EmitProgress(ConnectionAttemptPhase.Synchronizing);
            return Task.CompletedTask;
        }

        private void OnStopped() => Interlocked.Decrement(ref _activeCount);
    }

    private sealed class FakeAttempt(
        Guid profileId,
        long generation,
        Func<FakeAttempt, CancellationToken, Task> start,
        Action stopped) : IConnectionAttempt
    {
        private readonly TaskCompletionSource<ConnectionAttemptCompletion> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stopStarted;
        private int _stopped;
        public Guid ProfileId { get; } = profileId;
        public long Generation { get; } = generation;
        public Guid? SessionId { get; } = Guid.NewGuid();
        public Guid? NodeId => NodeIdValue;
        public Guid? NodeIdValue { get; set; }
        public Task<ConnectionAttemptCompletion> Completion => _completion.Task;
        public bool Stopped => Volatile.Read(ref _stopped) != 0;
        public TaskCompletionSource? StopGate { get; set; }
        public Exception? StopError { get; set; }
        public event EventHandler<ConnectionAttemptProgressEventArgs>? ProgressChanged;

        public Task StartAsync(CancellationToken cancellationToken = default) => start(this, cancellationToken);
        public async Task StopAsync(string reason, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _stopStarted, 1) != 0)
            {
                return;
            }

            if (StopGate is not null)
            {
                await StopGate.Task.WaitAsync(cancellationToken);
            }
            stopped();
            Volatile.Write(ref _stopped, 1);
            if (StopError is not null)
            {
                throw StopError;
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Fail(Exception exception) => _completion.TrySetResult(new ConnectionAttemptCompletion(exception));
        public void EmitProgress(ConnectionAttemptPhase phase) =>
            ProgressChanged?.Invoke(this, new ConnectionAttemptProgressEventArgs(Generation, phase));
    }

    private sealed class FakeReconnectDelay : IReconnectDelay
    {
        private readonly object _gate = new();
        public List<TimeSpan> History { get; } = [];
        public List<DelayRequest> Requests { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var request = new DelayRequest(delay, cancellationToken);
                History.Add(delay);
                Requests.Add(request);
                return request.Task;
            }
        }

        public void Complete(int index)
        {
            lock (_gate)
            {
                Requests[index].Complete();
            }
        }
    }

    private sealed class DelayRequest
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;
        public DelayRequest(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delay = delay;
            _registration = cancellationToken.Register(() =>
            {
                IsCanceled = true;
                _completion.TrySetCanceled(cancellationToken);
            });
        }
        public TimeSpan Delay { get; }
        public bool IsCanceled { get; private set; }
        public Task Task => _completion.Task;
        public void Complete()
        {
            _registration.Dispose();
            _completion.TrySetResult();
        }
    }

    private sealed class FakeJitter(IReadOnlyList<double> values) : IReconnectJitter
    {
        private int _index;
        public double GetJitterFraction(int retryNumber) =>
            values[Math.Min(Interlocked.Increment(ref _index) - 1, values.Count - 1)];
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FakePowerEvents : IPlatformPowerEvents
    {
        private EventHandler? _suspending;
        private EventHandler? _resumed;
        public int SubscriptionCount { get; private set; }
        public event EventHandler? Suspending
        {
            add { _suspending += value; SubscriptionCount++; }
            remove { _suspending -= value; SubscriptionCount--; }
        }
        public event EventHandler? Resumed
        {
            add { _resumed += value; SubscriptionCount++; }
            remove { _resumed -= value; SubscriptionCount--; }
        }
        public void Suspend() => _suspending?.Invoke(this, EventArgs.Empty);
        public void Resume() => _resumed?.Invoke(this, EventArgs.Empty);
    }
}
