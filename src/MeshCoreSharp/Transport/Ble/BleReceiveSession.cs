using System.Threading.Channels;
using MeshCoreSharp.Exceptions;

namespace MeshCoreSharp.Transport.Ble;

// One prepared generation with continuous receive and serialized writes.
internal sealed class BleReceiveSession : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly BleGattOpenResult _prepared;
    private readonly IBleGattNotifications _connection;
    private readonly BleMeshCoreTransportOptions _options;
    private readonly Channel<ReadOnlyMemory<byte>> _frames;
    private readonly TaskCompletionSource _terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopped;
    private bool _started;
    private Exception? _error;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly TimeProvider _time;
    private int _activeIo;
    private TaskCompletionSource? _idle;
    private bool _subscriptionAttempted;
    internal Exception? CleanupError { get; private set; }

    internal BleReceiveSession(BleGattOpenResult prepared, BleMeshCoreTransportOptions options, TimeProvider? time = null)
    {
        _prepared = prepared;
        _connection = prepared.Connection as IBleGattNotifications
            ?? throw new ArgumentException("The prepared connection must support notifications.", nameof(prepared));
        options.Validate();
        _options = options;
        _time = time ?? TimeProvider.System;
        _frames = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(options.ReceiveQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false,
            SingleReader = false, SingleWriter = false,
        });
    }

    internal bool IsConnected { get { lock (_sync) return !_stopped && _prepared.Attempt.State == BleConnectionState.Ready; } }
    internal Task CleanupCompletion { get; private set; } = Task.CompletedTask;
    internal Task Termination => _terminated.Task;
    internal Exception? Error { get { lock (_sync) return _error; } }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_started || _stopped) throw new InvalidOperationException("Create a receiver for each BLE generation.");
            _started = true;
            _connection.NotificationReceived += OnNotification;
            _connection.Disconnected += OnDisconnected;
            _connection.MaxPduSizeChanged += OnMtuChanged;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckLink();
            var subscription = StartNativeOperation(() =>
            {
                _subscriptionAttempted = true;
                return _connection.EnableNotificationsAsync(cancellationToken);
            });
            Observe(subscription);
            await Task.WhenAny(subscription, _terminated.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfStopped();
            await subscription.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            CheckLink();
            lock (_sync)
            {
                ThrowIfStopped();
                if (!_prepared.Attempt.TryMarkReadyAfterSubscription())
                    throw new InvalidOperationException("The BLE attempt closed before subscription completed.");
            }
        }
        catch (Exception error)
        {
            Stop(error);
            throw;
        }
    }

    internal IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _frames.Reader.ReadAllAsync(cancellationToken);

    internal async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        if (frame.IsEmpty || frame.Length > _options.MaximumFrameSize)
            throw new ArgumentOutOfRangeException(nameof(frame), "Send one nonempty complete Companion frame within the firmware limit.");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = false;
        using var deadline = new CancellationTokenSource(_options.WriteTimeout, _time);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ThrowIfStopped();
                if (!IsConnected) throw new MeshCoreTransportException("The BLE subscription is not ready.");
            }
            CheckLink();
            var native = _connection as IBleGattSession
                ?? throw new InvalidOperationException("The BLE connection does not support writes.");
            wait.Token.ThrowIfCancellationRequested();
            started = true;
            // Copy before native I/O: the caller may reuse its buffer after cancellation returns.
            var writing = StartNativeOperation(() => native.WriteAsync(frame.ToArray(), wait.Token));
            Observe(writing);
            await Task.WhenAny(writing, _terminated.Task).WaitAsync(wait.Token).ConfigureAwait(false);
            ThrowIfStopped();
            await writing.ConfigureAwait(false);
            wait.Token.ThrowIfCancellationRequested();
            ThrowIfStopped();
        }
        catch (Exception error)
        {
            if (started)
            {
                var failure = error is OperationCanceledException || _terminated.Task.IsCompleted
                    ? new MeshCoreBleTransportException(MeshCoreBleFailureReason.WriteOutcomeUnknown,
                        "The GATT write outcome is unknown. Reconnect before sending another frame; do not automatically repeat this write.", error)
                    : error;
                Stop(failure);
                if (cancellationToken.IsCancellationRequested && error is OperationCanceledException)
                    throw new OperationCanceledException(cancellationToken);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
            throw;
        }
        finally { _writeGate.Release(); }
    }

    private void CheckLink()
    {
        if (!_connection.IsLinkConnected) throw Failure(MeshCoreBleFailureReason.DeviceUnreachable, "The BLE link disconnected.");
        if (_connection.MaxPduSize < _options.MaximumFrameSize + BleGattProfile.AttributeValueOverhead)
            throw Failure(MeshCoreBleFailureReason.InsufficientMtu, "The BLE MTU is too small for complete Companion frames.");
    }

    private void OnNotification(ReadOnlyMemory<byte> frame)
    {
        Exception? failure = null;
        lock (_sync)
        {
            if (_stopped) return;
            if (frame.IsEmpty || frame.Length > _options.MaximumFrameSize)
                failure = Failure(MeshCoreBleFailureReason.InvalidFrame, "A BLE notification has an invalid Companion frame length.");
            else if (!_frames.Writer.TryWrite(frame.ToArray()))
                failure = Failure(MeshCoreBleFailureReason.ReceiveQueueOverflow, "The BLE receive queue is full.");
            if (failure is not null) Stop(failure);
        }
    }

    private void OnDisconnected(Exception? error) => Stop(error ?? Failure(
        MeshCoreBleFailureReason.DeviceUnreachable, "The BLE link disconnected."));
    private void OnMtuChanged(ushort size)
    {
        if (size < _options.MaximumFrameSize + BleGattProfile.AttributeValueOverhead)
            Stop(Failure(MeshCoreBleFailureReason.InsufficientMtu, "The BLE MTU decreased below the frame limit."));
    }

    internal void Stop(Exception? error = null)
    {
        lock (_sync)
        {
            if (_stopped) return;
            _stopped = true;
            _error = error;
            _frames.Writer.TryComplete(error);
            _terminated.TrySetResult();
            // The I/O reservation is made before invoking native code, even if it calls back synchronously.
            var idle = _idle?.Task ?? Task.CompletedTask;
            CleanupCompletion = Task.Run(() => CleanupAsync(idle, error));
            Observe(CleanupCompletion);
        }
    }

    private Task StartNativeOperation(Func<Task> start)
    {
        lock (_sync)
        {
            ThrowIfStopped();
            if (_activeIo++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task operation;
            try { operation = start(); }
            catch { EndNativeOperation(); throw; }
            _ = ObserveNativeCompletionAsync(operation);
            return operation;
        }
    }

    private async Task ObserveNativeCompletionAsync(Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch { /* The caller reports the primary outcome; late faults are observed here. */ }
        finally { lock (_sync) EndNativeOperation(); }
    }

    private void EndNativeOperation()
    {
        if (--_activeIo == 0) _idle!.TrySetResult();
    }

    private async Task CleanupAsync(Task idle, Exception? error)
    {
        var errors = new List<Exception>();
        void Detach(Action action)
        {
            try { action(); } catch (Exception failure) { errors.Add(failure); }
        }
        Detach(() => _connection.NotificationReceived -= OnNotification);
        Detach(() => _connection.Disconnected -= OnDisconnected);
        Detach(() => _connection.MaxPduSizeChanged -= OnMtuChanged);
        await idle.ConfigureAwait(false);
        if (_subscriptionAttempted && _connection.IsLinkConnected)
        {
            using var deadline = new CancellationTokenSource(_options.ShutdownTimeout, _time);
            Task? disabling = null;
            try
            {
                disabling = _connection.DisableNotificationsAsync(deadline.Token);
                await disabling.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                errors.Add(failure is OperationCanceledException && deadline.IsCancellationRequested
                    ? new TimeoutException("BLE CCCD disable exceeded the shutdown deadline.", failure) : failure);
                // Caller shutdown is bounded; physical cleanup retains ownership until native I/O ends.
                if (disabling is not null)
                    try { await disabling.ConfigureAwait(false); }
                    catch (Exception lateError) { if (!ReferenceEquals(failure, lateError)) errors.Add(lateError); }
            }
        }
        try { await _prepared.Attempt.CloseAsync(error).ConfigureAwait(false); }
        catch (Exception failure) { errors.Add(failure); }
        if (errors.Count != 0)
        {
            CleanupError = new AggregateException("BLE session cleanup failed.", errors);
            if (error is not null) error.Data["BleCleanupError"] = CleanupError;
            throw CleanupError;
        }
    }

    internal void ThrowIfStopped()
    {
        lock (_sync)
        {
            if (_stopped) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                _error ?? new MeshCoreTransportException("The BLE session is closed.")).Throw();
        }
    }

    internal static MeshCoreBleTransportException Failure(MeshCoreBleFailureReason reason, string message) => new(reason, message);
    internal static void Observe(Task task) => _ = task.ContinueWith(failed => { _ = failed.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    public async ValueTask DisposeAsync()
    {
        Stop();
        try { await CleanupCompletion.WaitAsync(_options.ShutdownTimeout, _time).ConfigureAwait(false); }
        catch (TimeoutException) { /* CleanupCompletion still owns and observes late native work. */ }
    }
}
