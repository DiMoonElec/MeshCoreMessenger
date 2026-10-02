using System.Threading.Channels;
using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Application;

public interface IConnectionSupervisor : IAsyncDisposable
{
    ConnectionSupervisorSnapshot Snapshot { get; }
    event EventHandler<ConnectionSupervisorStateChangedEventArgs>? StateChanged;

    Task StartAutoConnectAsync(CancellationToken cancellationToken = default);
    Task ConnectNowAsync(CancellationToken cancellationToken = default);
    Task SwitchProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

/// <summary>Serializes all connection ownership, retries and user lifecycle commands.</summary>
public sealed class ConnectionSupervisor : IConnectionSupervisor
{
    private static readonly TimeSpan StableOnlineDuration = TimeSpan.FromSeconds(60);

    private readonly IConnectionProfileManager _profiles;
    private readonly IConnectionAttemptFactory _attempts;
    private readonly IConnectionFailureClassifier _failures;
    private readonly IReconnectDelay _delay;
    private readonly IReconnectJitter _jitter;
    private readonly TimeProvider _timeProvider;
    private readonly IPlatformPowerEvents? _powerEvents;
    private readonly Channel<SupervisorMessage> _messages = Channel.CreateUnbounded<SupervisorMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _loop;
    private readonly List<TaskCompletionSource> _disconnectWaiters = [];
    private ConnectionSupervisorSnapshot _snapshot = new(
        ConnectionSupervisorState.Offline, 0, null, null, null, null, null);
    private ActiveAttempt? _active;
    private ConnectionProfile? _profile;
    private CancellationTokenSource? _retryCancellation;
    private long _retryId;
    private long _generation;
    private int _retryNumber;
    private bool _connectionDesired;
    private bool _suspended;
    private bool _resumeAfterWake;
    private string? _needsAttentionReason;
    private bool _exitRequested;
    private int _shutdownRequested;

    public ConnectionSupervisor(
        IConnectionProfileManager profiles,
        IConnectionAttemptFactory attempts,
        IConnectionFailureClassifier failures,
        IReconnectDelay delay,
        IReconnectJitter jitter,
        TimeProvider timeProvider,
        IPlatformPowerEvents? powerEvents = null)
    {
        _profiles = profiles;
        _attempts = attempts;
        _failures = failures;
        _delay = delay;
        _jitter = jitter;
        _timeProvider = timeProvider;
        _powerEvents = powerEvents;
        if (_powerEvents is not null)
        {
            _powerEvents.Suspending += OnSuspending;
            _powerEvents.Resumed += OnResumed;
        }
        _loop = Task.Run(RunAsync);
    }

    public ConnectionSupervisorSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public event EventHandler<ConnectionSupervisorStateChangedEventArgs>? StateChanged;

    public Task StartAutoConnectAsync(CancellationToken cancellationToken = default) =>
        SendControlAsync(ControlKind.StartAutoConnect, cancellationToken);

    public Task ConnectNowAsync(CancellationToken cancellationToken = default) =>
        SendControlAsync(ControlKind.ConnectNow, cancellationToken);

    public async Task SwitchProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Connection profile ID must not be empty.", nameof(profileId));
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _shutdownRequested) != 0, this);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _messages.Writer.WriteAsync(new SwitchProfileMessage(profileId, completion), cancellationToken)
            .ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        SendControlAsync(ControlKind.Disconnect, cancellationToken);

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _shutdownRequested, 1, 0) == 0)
        {
            if (!_messages.Writer.TryWrite(new ShutdownMessage()))
            {
                throw new ObjectDisposedException(nameof(ConnectionSupervisor));
            }
        }

        await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SendControlAsync(ControlKind kind, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _shutdownRequested) != 0, this);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _messages.Writer.WriteAsync(new ControlMessage(kind, completion), cancellationToken)
            .ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        await foreach (var message in _messages.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await HandleAsync(message).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _needsAttentionReason = exception.Message;
                Publish(ConnectionSupervisorState.NeedsAttention, exception.Message, nextAttemptUtc: null);
                if (message is ControlMessage control)
                {
                    control.Completion.TrySetException(exception);
                }
                else if (message is SwitchProfileMessage switchProfile)
                {
                    switchProfile.Completion.TrySetException(exception);
                }
            }

            if (_exitRequested)
            {
                break;
            }
        }

        _messages.Writer.TryComplete();
    }

    private Task HandleAsync(SupervisorMessage message) => message switch
    {
        ControlMessage control => HandleControlAsync(control),
        SwitchProfileMessage switchProfile => HandleSwitchProfileAsync(switchProfile),
        ShutdownMessage => HandleShutdownAsync(),
        PowerMessage power => HandlePowerAsync(power),
        AttemptProgressMessage progress => HandleProgressAsync(progress),
        AttemptOnlineMessage online => HandleOnlineAsync(online),
        AttemptEndedMessage ended => HandleAttemptEndedAsync(ended),
        TeardownCompletedMessage teardown => HandleTeardownCompletedAsync(teardown),
        RetryElapsedMessage retry => HandleRetryElapsedAsync(retry),
        _ => throw new ArgumentOutOfRangeException(nameof(message)),
    };

    private async Task HandleControlAsync(ControlMessage control)
    {
        switch (control.Kind)
        {
            case ControlKind.StartAutoConnect:
                await StartAutoConnectCoreAsync().ConfigureAwait(false);
                control.Completion.TrySetResult();
                break;
            case ControlKind.ConnectNow:
                await ConnectNowCoreAsync().ConfigureAwait(false);
                control.Completion.TrySetResult();
                break;
            case ControlKind.Disconnect:
                DisconnectCore(control.Completion);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(control));
        }
    }

    private async Task StartAutoConnectCoreAsync()
    {
        if (_active is not null || _retryCancellation is not null)
        {
            return;
        }

        var profile = await _profiles.GetSelectedProfileAsync().ConfigureAwait(false);
        _profile = profile;
        if (profile is null || !profile.AutoConnect)
        {
            _connectionDesired = false;
            Publish(ConnectionSupervisorState.Offline, null, null);
            return;
        }

        _connectionDesired = true;
        if (_suspended)
        {
            _resumeAfterWake = true;
            Publish(ConnectionSupervisorState.Offline, "System is suspended.", null);
            return;
        }
        await BeginAttemptAsync(profile).ConfigureAwait(false);
    }

    private async Task ConnectNowCoreAsync()
    {
        _connectionDesired = true;
        if (_suspended)
        {
            if (_profile is null)
            {
                _profile = await _profiles.GetSelectedProfileAsync().ConfigureAwait(false);
            }
            if (_profile is null)
            {
                _connectionDesired = false;
                _resumeAfterWake = false;
                _needsAttentionReason = "No connection profile is selected.";
                Publish(ConnectionSupervisorState.NeedsAttention, "No connection profile is selected.", null);
                return;
            }

            _resumeAfterWake = true;
            return;
        }

        if (_retryCancellation is not null)
        {
            CancelRetry();
            await BeginAttemptAsync(_profile!).ConfigureAwait(false);
            return;
        }

        if (_active is { } active)
        {
            if (active.RunCompleted && !active.TeardownStarted)
            {
                Publish(ConnectionSupervisorState.Disconnecting, "Replacing the current connection attempt.", null);
                BeginTeardown(active, TeardownIntent.Restart, "Reconnect requested");
            }
            return;
        }

        var profile = await _profiles.GetSelectedProfileAsync().ConfigureAwait(false);
        if (profile is null)
        {
            _connectionDesired = false;
            _needsAttentionReason = "No connection profile is selected.";
            Publish(ConnectionSupervisorState.NeedsAttention, "No connection profile is selected.", null);
            return;
        }

        _profile = profile;
        await BeginAttemptAsync(profile).ConfigureAwait(false);
    }

    private async Task HandleSwitchProfileAsync(SwitchProfileMessage message)
    {
        var profile = await _profiles.SelectAsync(message.ProfileId).ConfigureAwait(false);
        _needsAttentionReason = null;
        _profile = profile;
        _connectionDesired = true;
        _retryNumber = 0;
        CancelRetry();

        if (_suspended)
        {
            _resumeAfterWake = true;
            message.Completion.TrySetResult();
            return;
        }

        if (_active is null)
        {
            await BeginAttemptAsync(profile).ConfigureAwait(false);
            message.Completion.TrySetResult();
            return;
        }

        if (_active.Profile.Id == profile.Id && !_active.RunCompleted && !_active.TeardownStarted)
        {
            message.Completion.TrySetResult();
            return;
        }

        Publish(ConnectionSupervisorState.Disconnecting, "Switching connection profile.", null);
        _active.PendingIntent = TeardownIntent.Restart;
        _active.Cancellation.Cancel();
        if (_active.RunCompleted)
        {
            BeginTeardown(_active, TeardownIntent.Restart, "Connection profile changed");
        }
        message.Completion.TrySetResult();
    }

    private void DisconnectCore(TaskCompletionSource completion)
    {
        _connectionDesired = false;
        _resumeAfterWake = false;
        _needsAttentionReason = null;
        CancelRetry();
        if (_active is null)
        {
            Publish(ConnectionSupervisorState.Offline, null, null);
            completion.TrySetResult();
            return;
        }

        _disconnectWaiters.Add(completion);
        Publish(ConnectionSupervisorState.Disconnecting, "Disconnect requested.", null);
        _active.PendingIntent = TeardownIntent.Offline;
        _active.Cancellation.Cancel();
        if (_active.RunCompleted)
        {
            BeginTeardown(_active, TeardownIntent.Offline, "Disconnected by user");
        }
    }

    private Task HandleShutdownAsync()
    {
        UnsubscribePowerEvents();
        _connectionDesired = false;
        _resumeAfterWake = false;
        CancelRetry();
        if (_active is null)
        {
            FinishShutdown();
            return Task.CompletedTask;
        }

        Publish(ConnectionSupervisorState.Disconnecting, "Application shutdown.", null);
        _active.PendingIntent = TeardownIntent.Shutdown;
        _active.Cancellation.Cancel();
        if (_active.RunCompleted)
        {
            BeginTeardown(_active, TeardownIntent.Shutdown, "Application shutdown");
        }
        return Task.CompletedTask;
    }

    private Task HandlePowerAsync(PowerMessage power)
    {
        if (power.Suspending)
        {
            SuspendCore();
        }
        else
        {
            ResumeCore();
        }
        return Task.CompletedTask;
    }

    private void SuspendCore()
    {
        if (_suspended)
        {
            return;
        }

        _suspended = true;
        _resumeAfterWake = _connectionDesired && Snapshot.State != ConnectionSupervisorState.NeedsAttention;
        CancelRetry();
        if (_active is null)
        {
            Publish(
                _needsAttentionReason is null
                    ? ConnectionSupervisorState.Offline
                    : ConnectionSupervisorState.NeedsAttention,
                _needsAttentionReason ?? "System is suspended.",
                null,
                sessionId: null,
                nodeId: null);
            return;
        }

        Publish(ConnectionSupervisorState.Disconnecting, "System is suspending.", null);
        var intent = StrongerIntent(
            _active.PendingIntent ?? TeardownIntent.Suspend,
            TeardownIntent.Suspend);
        _active.PendingIntent = intent;
        _active.Cancellation.Cancel();
        if (_active.RunCompleted)
        {
            BeginTeardown(_active, intent, TeardownReason(intent));
        }
    }

    private void ResumeCore()
    {
        if (!_suspended)
        {
            return;
        }

        _suspended = false;
        if (!_resumeAfterWake || !_connectionDesired || _profile is null)
        {
            _resumeAfterWake = false;
            if (_active is null)
            {
                Publish(
                    _needsAttentionReason is null
                        ? ConnectionSupervisorState.Offline
                        : ConnectionSupervisorState.NeedsAttention,
                    _needsAttentionReason,
                    null,
                    sessionId: null,
                    nodeId: null);
            }
            return;
        }

        if (_active is null)
        {
            _resumeAfterWake = false;
            _ = BeginAttemptAsync(_profile);
        }
    }

    private Task BeginAttemptAsync(ConnectionProfile profile)
    {
        if (_active is not null)
        {
            throw new InvalidOperationException("A connection attempt is already active.");
        }

        var generation = checked(++_generation);
        _needsAttentionReason = null;
        Publish(
            ConnectionSupervisorState.Connecting,
            null,
            null,
            generation,
            profile.Id,
            sessionId: null,
            nodeId: null);

        var active = new ActiveAttempt(profile, generation, new CancellationTokenSource());
        _active = active;
        _ = CreateAndRunAttemptAsync(active);
        return Task.CompletedTask;
    }

    private async Task CreateAndRunAttemptAsync(ActiveAttempt active)
    {
        try
        {
            var attempt = await _attempts.CreateAsync(
                active.Profile,
                active.Generation,
                active.Cancellation.Token).ConfigureAwait(false);
            active.Attempt = attempt;
            active.ProgressHandler = (_, args) =>
                _messages.Writer.TryWrite(new AttemptProgressMessage(active, args.Generation, args.Phase));
            attempt.ProgressChanged += active.ProgressHandler;
            active.Cancellation.Token.ThrowIfCancellationRequested();
            await attempt.StartAsync(active.Cancellation.Token).ConfigureAwait(false);
            _messages.Writer.TryWrite(new AttemptOnlineMessage(active));
            var completion = await attempt.Completion.WaitAsync(active.Cancellation.Token).ConfigureAwait(false);
            _messages.Writer.TryWrite(new AttemptEndedMessage(
                active,
                completion.Error));
        }
        catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested)
        {
            _messages.Writer.TryWrite(new AttemptEndedMessage(
                active,
                null));
        }
        catch (Exception exception)
        {
            _messages.Writer.TryWrite(new AttemptEndedMessage(
                active,
                exception));
        }
    }

    private Task HandleProgressAsync(AttemptProgressMessage progress)
    {
        if (_suspended || !IsCurrent(progress.Active, progress.Generation) || progress.Active.RunCompleted || progress.Active.TeardownStarted)
        {
            return Task.CompletedTask;
        }

        var state = progress.Phase switch
        {
            ConnectionAttemptPhase.Identifying => ConnectionSupervisorState.Identifying,
            ConnectionAttemptPhase.Synchronizing => ConnectionSupervisorState.Synchronizing,
            _ => throw new ArgumentOutOfRangeException(nameof(progress)),
        };
        Publish(
            state,
            null,
            null,
            sessionId: progress.Active.Attempt?.SessionId,
            nodeId: progress.Active.Attempt?.NodeId);
        return Task.CompletedTask;
    }

    private Task HandleOnlineAsync(AttemptOnlineMessage online)
    {
        if (_suspended || !IsCurrent(online.Active, online.Active.Generation) || online.Active.TeardownStarted)
        {
            return Task.CompletedTask;
        }

        online.Active.OnlineSinceUtc = _timeProvider.GetUtcNow();
        Publish(
            ConnectionSupervisorState.Online,
            null,
            null,
            sessionId: online.Active.Attempt?.SessionId,
            nodeId: online.Active.Attempt?.NodeId);
        return Task.CompletedTask;
    }

    private Task HandleAttemptEndedAsync(AttemptEndedMessage ended)
    {
        if (!IsCurrent(ended.Active, ended.Active.Generation))
        {
            return Task.CompletedTask;
        }

        var active = ended.Active;
        active.RunCompleted = true;
        if (active.OnlineSinceUtc is { } onlineSince &&
            _timeProvider.GetUtcNow() - onlineSince >= StableOnlineDuration)
        {
            _retryNumber = 0;
        }

        if (active.PendingIntent is { } pendingIntent)
        {
            if (active.Attempt is null)
            {
                CompleteWithoutAttempt(active, pendingIntent);
            }
            else
            {
                BeginTeardown(active, pendingIntent, TeardownReason(pendingIntent));
            }
            return Task.CompletedTask;
        }

        var exception = ended.Error ?? new OperationCanceledException("The connection attempt ended unexpectedly.");
        active.Failure = exception;
        if (_failures.Classify(exception) == ConnectionFailureDisposition.NeedsAttention)
        {
            _needsAttentionReason = exception.Message;
            Publish(
                ConnectionSupervisorState.NeedsAttention,
                exception.Message,
                null,
                sessionId: active.Attempt?.SessionId,
                nodeId: active.Attempt?.NodeId);
            return Task.CompletedTask;
        }

        var intent = _connectionDesired && active.Profile.Reconnect
            ? TeardownIntent.Retry
            : TeardownIntent.Offline;
        if (active.Attempt is null)
        {
            CompleteWithoutAttempt(active, intent);
        }
        else
        {
            BeginTeardown(active, intent, "Connection attempt ended");
        }
        return Task.CompletedTask;
    }

    private void BeginTeardown(ActiveAttempt active, TeardownIntent intent, string reason)
    {
        if (active.TeardownStarted)
        {
            active.PendingIntent = StrongerIntent(active.PendingIntent ?? intent, intent);
            return;
        }

        active.TeardownStarted = true;
        active.PendingIntent = intent;
        active.Cancellation.Cancel();
        _ = TeardownAsync(active, reason);
    }

    private async Task TeardownAsync(ActiveAttempt active, string reason)
    {
        Exception? error = null;
        var attempt = active.Attempt!;
        try
        {
            await attempt.StopAsync(reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        try
        {
            await attempt.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            error ??= exception;
        }

        _messages.Writer.TryWrite(new TeardownCompletedMessage(
            active,
            active.PendingIntent ?? TeardownIntent.Offline,
            error));
    }

    private async Task HandleTeardownCompletedAsync(TeardownCompletedMessage teardown)
    {
        if (!IsCurrent(teardown.Active, teardown.Active.Generation))
        {
            return;
        }

        var active = teardown.Active;
        if (active.Attempt is not null && active.ProgressHandler is not null)
        {
            active.Attempt.ProgressChanged -= active.ProgressHandler;
        }
        active.Cancellation.Dispose();
        _active = null;

        if (teardown.Error is not null &&
            _failures.Classify(teardown.Error) == ConnectionFailureDisposition.NeedsAttention &&
            teardown.Intent is not TeardownIntent.Shutdown)
        {
            _connectionDesired = false;
            _needsAttentionReason = teardown.Error.Message;
            Publish(ConnectionSupervisorState.NeedsAttention, teardown.Error.Message, null, sessionId: null, nodeId: null);
            CompleteDisconnectWaiters();
            return;
        }

        switch (teardown.Intent)
        {
            case TeardownIntent.Retry:
                ScheduleRetry(teardown.Error?.Message ?? active.Failure?.Message ?? "Connection lost.");
                break;
            case TeardownIntent.Restart:
                await BeginAttemptAsync(_profile!).ConfigureAwait(false);
                break;
            case TeardownIntent.Suspend:
                await CompleteSuspendTeardownAsync(teardown.Error).ConfigureAwait(false);
                break;
            case TeardownIntent.Offline:
                Publish(ConnectionSupervisorState.Offline, teardown.Error?.Message, null, sessionId: null, nodeId: null);
                CompleteDisconnectWaiters();
                break;
            case TeardownIntent.Shutdown:
                FinishShutdown();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(teardown));
        }
    }

    private void CompleteWithoutAttempt(ActiveAttempt active, TeardownIntent intent)
    {
        active.Cancellation.Dispose();
        _active = null;
        switch (intent)
        {
            case TeardownIntent.Retry:
                ScheduleRetry(active.Failure?.Message ?? "Connection attempt failed.");
                break;
            case TeardownIntent.Restart:
                _ = BeginAttemptAsync(_profile!);
                break;
            case TeardownIntent.Suspend:
                _ = CompleteSuspendTeardownAsync(null);
                break;
            case TeardownIntent.Offline:
                Publish(ConnectionSupervisorState.Offline, active.Failure?.Message, null, sessionId: null, nodeId: null);
                CompleteDisconnectWaiters();
                break;
            case TeardownIntent.Shutdown:
                FinishShutdown();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(intent));
        }
    }

    private void ScheduleRetry(string reason)
    {
        if (_suspended)
        {
            _resumeAfterWake = _connectionDesired;
            Publish(ConnectionSupervisorState.Offline, "System is suspended.", null, sessionId: null, nodeId: null);
            return;
        }

        if (!_connectionDesired || _profile is null || !_profile.Reconnect)
        {
            Publish(ConnectionSupervisorState.Offline, reason, null, sessionId: null, nodeId: null);
            return;
        }

        TimeSpan delay;
        try
        {
            delay = ConnectionRetryPolicy.GetDelay(_retryNumber, _jitter);
        }
        catch (Exception exception)
        {
            _connectionDesired = false;
            _needsAttentionReason = exception.Message;
            Publish(ConnectionSupervisorState.NeedsAttention, exception.Message, null, sessionId: null, nodeId: null);
            return;
        }

        _retryNumber++;
        var retryId = ++_retryId;
        _retryCancellation = new CancellationTokenSource();
        Publish(
            ConnectionSupervisorState.RetryWaiting,
            reason,
            _timeProvider.GetUtcNow() + delay,
            sessionId: null,
            nodeId: null);
        _ = WaitForRetryAsync(retryId, delay, _retryCancellation.Token);
    }

    private async Task WaitForRetryAsync(long retryId, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await _delay.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            _messages.Writer.TryWrite(new RetryElapsedMessage(retryId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleRetryElapsedAsync(RetryElapsedMessage retry)
    {
        if (_suspended || _retryCancellation is null || retry.RetryId != _retryId || !_connectionDesired)
        {
            return;
        }

        CancelRetry();
        await BeginAttemptAsync(_profile!).ConfigureAwait(false);
    }

    private void CancelRetry()
    {
        var cancellation = Interlocked.Exchange(ref _retryCancellation, null);
        if (cancellation is null)
        {
            return;
        }

        ++_retryId;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private bool IsCurrent(ActiveAttempt active, long generation) =>
        ReferenceEquals(_active, active) && generation == active.Generation;

    private void Publish(
        ConnectionSupervisorState state,
        string? reason,
        DateTimeOffset? nextAttemptUtc,
        long? generation = null,
        Guid? profileId = null,
        Guid? sessionId = null,
        Guid? nodeId = null)
    {
        var previous = Snapshot;
        var current = new ConnectionSupervisorSnapshot(
            state,
            generation ?? previous.Generation,
            profileId ?? _profile?.Id ?? previous.ProfileId,
            sessionId,
            nodeId,
            reason,
            nextAttemptUtc) { UsedProfile = _active?.Profile ?? _profile };
        // Metadata must not introduce additional StateChanged publication points.
        if (current with { UsedProfile = null } == previous with { UsedProfile = null })
        {
            return;
        }

        Volatile.Write(ref _snapshot, current);
        var args = new ConnectionSupervisorStateChangedEventArgs(previous, current);
        foreach (EventHandler<ConnectionSupervisorStateChangedEventArgs> handler in
                 StateChanged?.GetInvocationList().Cast<EventHandler<ConnectionSupervisorStateChangedEventArgs>>() ?? [])
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // UI/state observers must never terminate the supervisor loop.
            }
        }
    }

    private void CompleteDisconnectWaiters()
    {
        foreach (var waiter in _disconnectWaiters)
        {
            waiter.TrySetResult();
        }
        _disconnectWaiters.Clear();
    }

    private void FinishShutdown()
    {
        Publish(ConnectionSupervisorState.Offline, null, null, sessionId: null, nodeId: null);
        CompleteDisconnectWaiters();
        _exitRequested = true;
    }

    private async Task CompleteSuspendTeardownAsync(Exception? error)
    {
        if (_suspended || !_resumeAfterWake || !_connectionDesired || _profile is null)
        {
            Publish(
                _needsAttentionReason is null
                    ? ConnectionSupervisorState.Offline
                    : ConnectionSupervisorState.NeedsAttention,
                _needsAttentionReason ?? error?.Message ?? "System is suspended.",
                null,
                sessionId: null,
                nodeId: null);
            return;
        }

        _resumeAfterWake = false;
        await BeginAttemptAsync(_profile).ConfigureAwait(false);
    }

    private void OnSuspending(object? sender, EventArgs args) =>
        _messages.Writer.TryWrite(new PowerMessage(true));

    private void OnResumed(object? sender, EventArgs args) =>
        _messages.Writer.TryWrite(new PowerMessage(false));

    private void UnsubscribePowerEvents()
    {
        if (_powerEvents is null)
        {
            return;
        }

        _powerEvents.Suspending -= OnSuspending;
        _powerEvents.Resumed -= OnResumed;
    }

    private static string TeardownReason(TeardownIntent intent) => intent switch
    {
        TeardownIntent.Offline => "Disconnected by user",
        TeardownIntent.Restart => "Reconnect requested",
        TeardownIntent.Retry => "Connection failed",
        TeardownIntent.Suspend => "System suspend",
        TeardownIntent.Shutdown => "Application shutdown",
        _ => "Connection attempt ended",
    };

    private static TeardownIntent StrongerIntent(TeardownIntent current, TeardownIntent requested)
    {
        if (requested == TeardownIntent.Shutdown || current == TeardownIntent.Shutdown)
        {
            return TeardownIntent.Shutdown;
        }
        if (requested == TeardownIntent.Offline || current == TeardownIntent.Offline)
        {
            return TeardownIntent.Offline;
        }
        if (requested == TeardownIntent.Suspend || current == TeardownIntent.Suspend)
        {
            return TeardownIntent.Suspend;
        }
        return requested;
    }

    private sealed class ActiveAttempt(
        ConnectionProfile profile,
        long generation,
        CancellationTokenSource cancellation)
    {
        public ConnectionProfile Profile { get; } = profile;
        public long Generation { get; } = generation;
        public IConnectionAttempt? Attempt { get; set; }
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public EventHandler<ConnectionAttemptProgressEventArgs>? ProgressHandler { get; set; }
        public DateTimeOffset? OnlineSinceUtc { get; set; }
        public bool RunCompleted { get; set; }
        public bool TeardownStarted { get; set; }
        public TeardownIntent? PendingIntent { get; set; }
        public Exception? Failure { get; set; }
    }

    private abstract record SupervisorMessage;
    private sealed record ControlMessage(ControlKind Kind, TaskCompletionSource Completion) : SupervisorMessage;
    private sealed record SwitchProfileMessage(Guid ProfileId, TaskCompletionSource Completion) : SupervisorMessage;
    private sealed record ShutdownMessage : SupervisorMessage;
    private sealed record PowerMessage(bool Suspending) : SupervisorMessage;
    private sealed record AttemptProgressMessage(ActiveAttempt Active, long Generation, ConnectionAttemptPhase Phase) : SupervisorMessage;
    private sealed record AttemptOnlineMessage(ActiveAttempt Active) : SupervisorMessage;
    private sealed record AttemptEndedMessage(ActiveAttempt Active, Exception? Error) : SupervisorMessage;
    private sealed record TeardownCompletedMessage(ActiveAttempt Active, TeardownIntent Intent, Exception? Error) : SupervisorMessage;
    private sealed record RetryElapsedMessage(long RetryId) : SupervisorMessage;

    private enum ControlKind { StartAutoConnect, ConnectNow, Disconnect }
    private enum TeardownIntent { Retry, Restart, Suspend, Offline, Shutdown }
}
