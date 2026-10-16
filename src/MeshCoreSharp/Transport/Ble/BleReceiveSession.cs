using System.Threading.Channels;
using System.Runtime.CompilerServices;
using System.Text;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Models;

namespace MeshCoreSharp.Transport.Ble;

// One prepared generation with continuous receive and serialized writes.
internal sealed class BleReceiveSession : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly BleGattOpenResult _prepared;
    private readonly IBleGattNotifications _connection;
    private readonly BleMeshCoreTransportOptions _options;
    private readonly record struct Notification(ReadOnlyMemory<byte> Frame, int PayloadCapacity, long Sequence);
    private readonly Channel<Notification> _frames;
    private long _notificationSequence;
    private int _consumer;
    private string? _senderName;
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
        _frames = Channel.CreateBounded<Notification>(new BoundedChannelOptions(options.ReceiveQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false,
            SingleReader = false, SingleWriter = false,
        });
    }

    internal bool IsConnected { get { lock (_sync) return !_stopped && _prepared.Attempt.State == BleConnectionState.Ready; } }
    internal Task CleanupCompletion { get; private set; } = Task.CompletedTask;
    internal Task Termination => _terminated.Task;
    internal Exception? Error { get { lock (_sync) return _error; } }
    internal ushort? MaxPduSize => IsConnected ? _connection.MaxPduSize : null;

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

    internal void SetSenderName(string name) { lock (_sync) _senderName = name; }

    internal BleTextBudget GetTextBudget(BleTextMessageKind kind, string? senderName = null, bool regionScoped = true)
    {
        lock (_sync)
        {
            ThrowIfStopped();
            if (!IsConnected) throw new InvalidOperationException("The BLE subscription is not ready.");
            return BleTextBudget.Calculate(_connection.MaxPduSize, kind, senderName ?? _senderName,
                regionScoped, _options.MaximumFrameSize);
        }
    }

    internal async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _consumer, 1) != 0)
            throw new InvalidOperationException("Only one BLE receive consumer is supported per generation.");
        try
        {
            await foreach (var notification in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return await CompleteNotificationAsync(notification, cancellationToken).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref _consumer, 0); }
    }

    private async Task<ReadOnlyMemory<byte>> CompleteNotificationAsync(Notification notification, CancellationToken cancellationToken)
    {
        var frame = notification.Frame;
        // A notification that fills ATT_MTU-3 is indistinguishable from a truncated longer value.
        // Read the complete retained TX value; never concatenate independent notifications.
        if (frame.Length == notification.PayloadCapacity && frame.Length < _options.MaximumFrameSize)
        {
            using var deadline = new CancellationTokenSource(_options.NotificationReadTimeout, _time);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            try
            {
                if (_connection is not IBleGattValueReader reader)
                    throw Failure(MeshCoreBleFailureReason.IncompleteNotification,
                        "A full-size BLE notification may be truncated and the TX characteristic cannot be read.");
                var reading = StartNativeRead(reader, notification, wait.Token);
                await Task.WhenAny(reading, _terminated.Task).WaitAsync(wait.Token).ConfigureAwait(false);
                ThrowIfStopped();
                var complete = await reading.ConfigureAwait(false);
                wait.Token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    ThrowIfStopped();
                    if (_notificationSequence != notification.Sequence || complete.Length < frame.Length
                        || complete.Length > _options.MaximumFrameSize || !complete.Span[..frame.Length].SequenceEqual(frame.Span))
                        throw Failure(MeshCoreBleFailureReason.IncompleteNotification,
                            "The full BLE TX value changed or could not be matched to its notification; partial data was not delivered.");
                    frame = complete.ToArray();
                }
            }
            catch (Exception error)
            {
                var failure = error is MeshCoreBleTransportException ? error : new MeshCoreBleTransportException(
                    MeshCoreBleFailureReason.IncompleteNotification,
                    "The full BLE notification could not be verified; partial data was not delivered.", error);
                Stop(failure);
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        try { ValidateTextEncoding(frame.Span); }
        catch (DecoderFallbackException error)
        {
            var failure = new MeshCoreBleTransportException(MeshCoreBleFailureReason.IncompleteNotification,
                "A BLE text frame contains incomplete/invalid UTF-8; partial text was not delivered.", error);
            Stop(failure);
            throw failure;
        }
        return frame;
    }

    private Task<ReadOnlyMemory<byte>> StartNativeRead(IBleGattValueReader reader, Notification notification, CancellationToken token)
    {
        Task<ReadOnlyMemory<byte>>? reading = null;
        _ = StartNativeOperation(() =>
        {
            if (_notificationSequence != notification.Sequence)
                throw Failure(MeshCoreBleFailureReason.IncompleteNotification,
                    "A newer notification replaced the retained BLE TX value before it could be verified.");
            return reading = reader.ReadNotificationValueAsync(token);
        });
        return reading!;
    }

    private static void ValidateTextEncoding(ReadOnlySpan<byte> frame)
    {
        var offset = (PacketType)frame[0] switch
        {
            PacketType.ContactMessageReceived => BleProtocolLimits.LegacyPrivateTextHeader,
            PacketType.ContactMessageReceivedV3 => BleProtocolLimits.V3PrivateTextHeader,
            PacketType.ChannelMessageReceived => BleProtocolLimits.LegacyChannelTextHeader,
            PacketType.ChannelMessageReceivedV3 => BleProtocolLimits.V3ChannelTextHeader,
            _ => -1,
        };
        if (offset < 0 || frame.Length < offset) return; // Normal decoder diagnoses incomplete headers.
        var textType = (MessageTextType)frame[offset - BleProtocolLimits.TextTypeAndTimestamp];
        if (textType is not (MessageTextType.Plain or MessageTextType.SignedPlain)) return;
        if ((PacketType)frame[0] is PacketType.ContactMessageReceived or PacketType.ContactMessageReceivedV3
            && textType == MessageTextType.SignedPlain)
            offset += ProtocolLimits.MessageSenderPrefixSize;
        if (frame.Length < offset) return;
        var text = frame[offset..];
        var nul = text.IndexOf((byte)0);
        if (nul >= 0) text = text[..nul];
        _ = new UTF8Encoding(false, true).GetCharCount(text);
    }

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
            // Copy before native I/O: the caller may reuse its buffer after cancellation returns.
            var writing = StartNativeOperation(() =>
            {
                ValidateOutgoing(frame.Span);
                started = true;
                return native.WriteAsync(frame.ToArray(), wait.Token);
            });
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
        if (_connection.MaxPduSize < BleGattProfile.MinimumAttMtu)
            throw Failure(MeshCoreBleFailureReason.InsufficientMtu, "The BLE ATT MTU is invalid.");
    }

    private void ValidateOutgoing(ReadOnlySpan<byte> frame)
    {
        var payload = Math.Min(_options.MaximumFrameSize, _connection.MaxPduSize - BleGattProfile.AttributeValueOverhead);
        if (frame.Length > payload)
            throw new ArgumentOutOfRangeException(nameof(frame), $"The BLE frame has {frame.Length} bytes; the current single-write budget is {payload} bytes.");
        var type = (CommandType)frame[0];
        if (type == CommandType.SendTextMessage && frame.Length >= BleProtocolLimits.PrivateTextHeader)
        {
            if (frame.Length - BleProtocolLimits.PrivateTextHeader > GetTextBudget(BleTextMessageKind.Private).MaximumTextBytes)
                throw new ArgumentOutOfRangeException(nameof(frame), "The private text exceeds the BLE UTF-8 byte budget.");
        }
        else if (type == CommandType.SendChannelTextMessage && frame.Length >= BleProtocolLimits.ChannelTextHeader)
        {
            if (_senderName is null) throw new InvalidOperationException("BLE channel text requires the current SelfInfo sender name.");
            if (frame.Length - BleProtocolLimits.ChannelTextHeader > GetTextBudget(BleTextMessageKind.Channel).MaximumTextBytes)
                throw new ArgumentOutOfRangeException(nameof(frame), "The channel text exceeds the BLE UTF-8 byte budget, including the sender prefix.");
        }
    }

    private void OnNotification(ReadOnlyMemory<byte> frame)
    {
        Exception? failure = null;
        lock (_sync)
        {
            if (_stopped) return;
            if (frame.IsEmpty || frame.Length > _options.MaximumFrameSize)
                failure = Failure(MeshCoreBleFailureReason.InvalidFrame, "A BLE notification has an invalid Companion frame length.");
            else if (!_frames.Writer.TryWrite(new(frame.ToArray(),
                _connection.MaxPduSize - BleGattProfile.AttributeValueOverhead, ++_notificationSequence)))
                failure = Failure(MeshCoreBleFailureReason.ReceiveQueueOverflow, "The BLE receive queue is full.");
            if (failure is not null) Stop(failure);
        }
    }

    private void OnDisconnected(Exception? error) => Stop(error ?? Failure(
        MeshCoreBleFailureReason.DeviceUnreachable, "The BLE link disconnected."));
    private void OnMtuChanged(ushort size)
    {
        if (size < BleGattProfile.MinimumAttMtu)
            Stop(Failure(MeshCoreBleFailureReason.InsufficientMtu, "The BLE ATT MTU decreased below its minimum."));
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
