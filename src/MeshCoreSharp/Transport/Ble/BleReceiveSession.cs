using System.Threading.Channels;
using MeshCoreSharp.Exceptions;

namespace MeshCoreSharp.Transport.Ble;

// One prepared generation. This is deliberately internal until the send side is available.
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

    internal BleReceiveSession(BleGattOpenResult prepared, BleMeshCoreTransportOptions options)
    {
        _prepared = prepared;
        _connection = prepared.Connection as IBleGattNotifications
            ?? throw new ArgumentException("The prepared connection must support notifications.", nameof(prepared));
        options.Validate();
        _options = options;
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
            var subscription = _connection.EnableNotificationsAsync(cancellationToken);
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
            // Native cleanup can wait for an uninterruptible CCCD operation. Never run it on a GATT callback.
            CleanupCompletion = Task.Run(async () =>
            {
                _connection.NotificationReceived -= OnNotification;
                _connection.Disconnected -= OnDisconnected;
                _connection.MaxPduSizeChanged -= OnMtuChanged;
                await _prepared.Attempt.CloseAsync(error).ConfigureAwait(false);
            });
            Observe(CleanupCompletion);
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

    public ValueTask DisposeAsync()
    {
        Stop();
        return new(CleanupCompletion);
    }
}
