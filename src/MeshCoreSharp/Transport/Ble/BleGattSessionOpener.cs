using MeshCoreSharp.Exceptions;

namespace MeshCoreSharp.Transport.Ble;

internal interface IBleGattOpenPlatform
{
    // Owns all native resources until returning a connection; failure cleans up partial results.
    // Cancellation is checked between native operations, not by abandoning their resource results.
    Task<IBleGattConnection?> OpenAsync(string deviceId, CancellationToken cancellationToken);
}

internal sealed class BleGattOpenResult(BleConnectionAttempt attempt, IBleGattConnection connection) : IAsyncDisposable
{
    internal BleConnectionAttempt Attempt { get; } = attempt;
    internal IBleGattConnection Connection { get; } = connection;
    public ValueTask DisposeAsync() => Attempt.DisposeAsync();
}

internal sealed class BleGattSessionOpener(IBleGattOpenPlatform platform, TimeProvider time)
{
    private int _started;
    // One opener per attempt. A timed-out native operation can still own resources until this completes.
    internal Task CleanupCompletion { get; private set; } = Task.CompletedTask;
    internal async Task<BleGattOpenResult> OpenAsync(BleMeshCoreTransportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Create a new opener for each BLE attempt.");
        var attempt = new BleConnectionAttempt(options);
        attempt.BeginOpening();
        using var deadline = new CancellationTokenSource(options.OpenTimeout, time);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        Task<IBleGattConnection?>? opening = null;
        var adopted = false;
        var waitingForMtu = false;
        try
        {
            opening = platform.OpenAsync(options.DeviceId, lifetime.Token);
            var connection = await opening.WaitAsync(lifetime.Token).ConfigureAwait(false);
            if (connection is null)
                throw new MeshCoreBleTransportException(MeshCoreBleFailureReason.DeviceUnreachable,
                    "The selected BLE endpoint is unavailable.");
            adopted = await attempt.AdoptSessionAsync(connection).ConfigureAwait(false);
            if (!adopted)
                throw new InvalidOperationException("The BLE attempt was already closed.");
            lifetime.Token.ThrowIfCancellationRequested();
            ValidateCapabilities(connection.Capabilities);
            waitingForMtu = true;
            await WaitForMtuAsync(connection,
                checked((ushort)(options.MaximumFrameSize + BleGattProfile.AttributeValueOverhead)),
                lifetime.Token).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            // Still Opening: only BLE5 may mark Ready after successful CCCD subscription.
            return new(attempt, connection);
        }
        catch (Exception error)
        {
            var failure = error;
            if (error is OperationCanceledException)
                failure = cancellationToken.IsCancellationRequested
                    ? new OperationCanceledException(cancellationToken)
                    : deadline.IsCancellationRequested
                        ? new MeshCoreBleTransportException(waitingForMtu
                                ? MeshCoreBleFailureReason.InsufficientMtu : MeshCoreBleFailureReason.DeviceUnreachable,
                            waitingForMtu ? "The BLE MTU did not reach the required frame size before the open deadline."
                                : "Opening the BLE endpoint timed out.", error)
                        : error;
            var cleanup = attempt.CloseAsync(failure);
            CleanupCompletion = opening is not null && !adopted
                ? DisposeLateResultAsync(opening, attempt, cleanup) : cleanup;
            Observe(CleanupCompletion);
            try { await cleanup.ConfigureAwait(false); }
            catch (Exception cleanupError) { failure.Data["BleCleanupError"] = cleanupError; }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    private static void ValidateCapabilities(BleGattCapabilities capabilities)
    {
        if (!capabilities.HasCompanionService)
            throw new MeshCoreBleTransportException(MeshCoreBleFailureReason.MissingService, "The NUS service is missing.");
        if (!capabilities.HasAppToCompanionCharacteristic || !capabilities.HasCompanionToAppCharacteristic)
            throw new MeshCoreBleTransportException(MeshCoreBleFailureReason.MissingCharacteristic,
                "The NUS RX or TX characteristic is missing.");
        if (!capabilities.CanWriteWithResponse || !capabilities.CanNotify)
            throw new MeshCoreBleTransportException(MeshCoreBleFailureReason.UnsupportedCharacteristics,
                "The NUS characteristics must support WriteWithResponse and Notify.");
    }

    private static async Task WaitForMtuAsync(IBleGattConnection connection, ushort minimum,
        CancellationToken cancellationToken)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Observe(ready.Task);
        void CheckMtu(ushort size) { if (size >= minimum) ready.TrySetResult(); }
        void OnDisconnect(Exception? error) => ready.TrySetException(error ?? new MeshCoreBleTransportException(
            MeshCoreBleFailureReason.DeviceUnreachable, "The BLE link disconnected while opening."));
        connection.MaxPduSizeChanged += CheckMtu;
        connection.Disconnected += OnDisconnect;
        try
        {
            if (!connection.IsLinkConnected) OnDisconnect(null);
            CheckMtu(connection.MaxPduSize);
            await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Also check a link drop after the MTU event won the completion race.
            if (!connection.IsLinkConnected)
                throw new MeshCoreBleTransportException(MeshCoreBleFailureReason.DeviceUnreachable,
                    "The BLE link disconnected while opening.");
            if (connection.MaxPduSize < minimum)
                throw new MeshCoreBleTransportException(MeshCoreBleFailureReason.InsufficientMtu,
                    "The BLE MTU decreased while opening.");
        }
        finally
        {
            connection.MaxPduSizeChanged -= CheckMtu;
            connection.Disconnected -= OnDisconnect;
        }
    }

    private static async Task DisposeLateResultAsync(Task<IBleGattConnection?> opening,
        BleConnectionAttempt attempt, Task cleanup)
    {
        await cleanup.ConfigureAwait(false);
        IBleGattConnection? connection;
        try
        {
            connection = await opening.ConfigureAwait(false);
        }
        catch
        {
            // Open failure owns partial cleanup. Await observes the late failure after caller cancellation.
            return;
        }
        if (connection is not null) await attempt.AdoptSessionAsync(connection).ConfigureAwait(false);
    }

    private static void Observe(Task task) => _ = task.ContinueWith(
        failed => { _ = failed.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
