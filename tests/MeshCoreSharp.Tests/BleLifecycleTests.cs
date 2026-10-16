using MeshCoreSharp;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Transport.Ble;

internal static class BleLifecycleTests
{
    internal static (string Name, Func<Task> Run)[] Cases =>
    [
        ("BLE lifecycle disconnects during native open and isolates a late result after reconnect", PendingOpen),
        ("BLE lifecycle disconnects during MTU wait and releases the prepared link", PendingMtu),
        ("BLE lifecycle disconnects during CCCD and retains native ownership until completion", PendingSubscribe),
        ("BLE lifecycle closes active and queued writes without disposing under native I/O", PendingWrite),
        ("BLE lifecycle bounds CCCD disable and observes its late failure before disposal", PendingDisable),
        ("BLE lifecycle failed open can reconnect and disposed transport cannot reopen", FailedOpenAndDispose),
        ("BLE lifecycle unplug faults RX and explicit reconnect creates a fresh queue", Unplug),
        ("BLE lifecycle cleanup failure remains observable and does not poison the next generation", CleanupFailure),
        ("BLE lifecycle disconnect cancellation limits only the caller wait", CancelDisconnect),
        ("BLE lifecycle failed preparation bounds disposal and observes a late release", SlowPreparationCleanup),
        ("BLE lifecycle reserves native ownership before a synchronous write callback", SynchronousDisconnect),
        ("BLE lifecycle reports partial cleanup errors from a late failed native open", LatePartialFailure),
        ("BLE lifecycle client disposal ignores blocked user callbacks and cancels a pending command", ClientDisposal),
    ];

    private static void Check(bool value) => BleReceiveTests.Check(value);
    private static Task<T> Throws<T>(Func<Task> action) where T : Exception => BleReceiveTests.Throws<T>(action);
    private static BleMeshCoreTransportOptions Options => BleReceiveTests.Options() with
    {
        OpenTimeout = TimeSpan.FromSeconds(10), ShutdownTimeout = TimeSpan.FromSeconds(1),
    };
    private static async Task<FakeBleGattSession> Connect(BleFrameTransport transport, FakeBleGattOpenPlatform platform,
        FakeBleGattSession? native = null)
    {
        var connecting = transport.ConnectAsync();
        var open = await platform.Opens.Reader.ReadAsync();
        native ??= new();
        open.Operation.Complete(native);
        (await native.Subscriptions.Reader.ReadAsync()).Complete(true);
        await connecting.WaitAsync(TimeSpan.FromSeconds(5));
        return native;
    }
    private static async Task ExpireShutdown(BleDiscoveryTestClock clock, Task shutdown)
    {
        await clock.WaitForTimerAsync(Options.ShutdownTimeout);
        clock.Advance(Options.ShutdownTimeout);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task PendingOpen()
    {
        var platform = new FakeBleGattOpenPlatform();
        var clock = new BleDiscoveryTestClock();
        await using var transport = new BleFrameTransport(Options, platform, clock);
        var opening = transport.ConnectAsync();
        var pending = await platform.Opens.Reader.ReadAsync();
        var closing = transport.DisconnectAsync();
        await Throws<OperationCanceledException>(() => opening);
        await ExpireShutdown(clock, closing);
        var oldCleanup = transport.CleanupCompletion;
        Check(!oldCleanup.IsCompleted && !transport.IsConnected);
        var next = await Connect(transport, platform);
        var late = new FakeBleGattSession();
        pending.Operation.Complete(late);
        await oldCleanup.WaitAsync(TimeSpan.FromSeconds(5));
        Check(late.DisposeCount == 1 && next.DisposeCount == 0 && transport.IsConnected);
        await transport.DisconnectAsync();
        Check(next.DisposeCount == 1);
    }

    private static async Task PendingMtu()
    {
        var platform = new FakeBleGattOpenPlatform();
        await using var transport = new BleFrameTransport(Options, platform, TimeProvider.System);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new FakeBleGattSession { OnMtuRead = () => read.TrySetResult() };
        native.EmitMaxPduSize(0);
        var opening = transport.ConnectAsync();
        (await platform.Opens.Reader.ReadAsync()).Operation.Complete(native);
        await read.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transport.DisconnectAsync();
        await Throws<OperationCanceledException>(() => opening);
        await transport.CleanupCompletion;
        Check(native.DisposeCount == 1 && !native.HasPreparationHandlers && !native.Subscriptions.Reader.TryRead(out _));
    }

    private static async Task PendingSubscribe()
    {
        var platform = new FakeBleGattOpenPlatform();
        var clock = new BleDiscoveryTestClock();
        await using var transport = new BleFrameTransport(Options, platform, clock);
        var opening = transport.ConnectAsync();
        var native = new FakeBleGattSession();
        (await platform.Opens.Reader.ReadAsync()).Operation.Complete(native);
        var pending = await native.Subscriptions.Reader.ReadAsync();
        await ExpireShutdown(clock, transport.DisconnectAsync());
        var failure = await Throws<Exception>(() => opening);
        Check(failure is OperationCanceledException or MeshCoreTransportException);
        Check(native.DisposeCount == 0 && !transport.IsConnected);
        pending.Complete(true);
        await transport.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(native.DisposeCount == 1 && native.Unsubscriptions.Reader.TryRead(out _));
    }

    private static async Task PendingWrite()
    {
        var platform = new FakeBleGattOpenPlatform();
        var clock = new BleDiscoveryTestClock();
        await using var transport = new BleFrameTransport(Options, platform, clock);
        var native = await Connect(transport, platform);
        var retained = native.CaptureCallbacks();
        var send = transport.SendAsync(new byte[] { 1 }).AsTask();
        var writing = await native.Writes.Reader.ReadAsync();
        var queued = transport.SendAsync(new byte[] { 2 }).AsTask();
        await using var reader = transport.ReceiveAsync().GetAsyncEnumerator();
        var read = reader.MoveNextAsync().AsTask();
        await ExpireShutdown(clock, transport.DisconnectAsync());
        await Throws<MeshCoreBleTransportException>(() => send);
        await Throws<MeshCoreTransportException>(() => queued);
        Check(!await read && native.DisposeCount == 0 && !native.Unsubscriptions.Reader.TryRead(out _));
        var oldCleanup = transport.CleanupCompletion;
        var next = await Connect(transport, platform);
        retained.Notification?.Invoke(new byte[] { 0xFE });
        retained.Disconnect?.Invoke(null);
        retained.Mtu?.Invoke(23);
        writing.Operation.Complete(true);
        await oldCleanup.WaitAsync(TimeSpan.FromSeconds(5));
        Check(native.DisposeCount == 1 && transport.IsConnected && !next.Writes.Reader.TryRead(out _));
    }

    private static async Task PendingDisable()
    {
        var platform = new FakeBleGattOpenPlatform();
        var clock = new BleDiscoveryTestClock();
        var transport = new BleFrameTransport(Options, platform, clock);
        var native = await Connect(transport, platform, new() { AutoCompleteUnsubscriptions = false });
        var closing = transport.DisconnectAsync();
        var disabling = await native.Unsubscriptions.Reader.ReadAsync();
        clock.Advance(Options.ShutdownTimeout);
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Check(disabling.CancellationToken.IsCancellationRequested && native.DisposeCount == 0);
        var cleanup = transport.CleanupCompletion;
        Check(!cleanup.IsCompleted);
        disabling.Fail(new IOException("Late CCCD disable failure"));
        var error = await Throws<AggregateException>(() => cleanup);
        Check(error.ToString().Contains("Late CCCD disable failure") && native.DisposeCount == 1);
        await Throws<AggregateException>(() => transport.DisposeAsync().AsTask());
    }

    private static async Task FailedOpenAndDispose()
    {
        foreach (var timeout in new[] { TimeSpan.Zero, Timeout.InfiniteTimeSpan, TimeSpan.MaxValue })
            await Throws<ArgumentOutOfRangeException>(() => { (Options with { ShutdownTimeout = timeout }).Validate(); return Task.CompletedTask; });
        var platform = new FakeBleGattOpenPlatform();
        var transport = new BleFrameTransport(Options, platform, TimeProvider.System);
        var open = transport.ConnectAsync();
        (await platform.Opens.Reader.ReadAsync()).Operation.Fail(new IOException("Native discovery failure"));
        await Throws<IOException>(() => open);
        var native = await Connect(transport, platform);
        await transport.ConnectAsync(); // Already Ready is idempotent.
        Check(!platform.Opens.Reader.TryRead(out _));
        await transport.DisposeAsync();
        await transport.DisposeAsync();
        Check(native.DisposeCount == 1);
        await Throws<ObjectDisposedException>(() => transport.ConnectAsync());
    }

    private static async Task Unplug()
    {
        var platform = new FakeBleGattOpenPlatform();
        await using var transport = new BleFrameTransport(Options, platform, TimeProvider.System);
        var native = await Connect(transport, platform);
        await using var reader = transport.ReceiveAsync().GetAsyncEnumerator();
        var reading = reader.MoveNextAsync().AsTask();
        native.EmitDisconnect(new MeshCoreBleTransportException(MeshCoreBleFailureReason.RadioOff, "Fake radio off"));
        var error = await Throws<MeshCoreBleTransportException>(() => reading);
        Check(error.Reason == MeshCoreBleFailureReason.RadioOff && !transport.IsConnected);
        var next = await Connect(transport, platform);
        next.EmitNotification(new byte[] { 0 });
        await using var nextReader = transport.ReceiveAsync().GetAsyncEnumerator();
        Check(await nextReader.MoveNextAsync() && nextReader.Current.Span[0] == 0);
    }

    private static async Task CleanupFailure()
    {
        var platform = new FakeBleGattOpenPlatform();
        var transport = new BleFrameTransport(Options, platform, TimeProvider.System);
        var disposal = new ControlledBleOperation<bool>(default);
        var native = await Connect(transport, platform, new() { Disposal = disposal });
        var close = transport.DisconnectAsync();
        disposal.Fail(new IOException("Fake native release failure"));
        await Throws<AggregateException>(() => close);
        Check(native.DisposeCount == 1);
        var previous = transport.CleanupCompletion;
        var next = await Connect(transport, platform);
        await transport.DisposeAsync();
        Check(next.DisposeCount == 1);
        await Throws<AggregateException>(() => previous);
        await Throws<AggregateException>(() => transport.CleanupCompletion);
    }

    private static async Task CancelDisconnect()
    {
        var platform = new FakeBleGattOpenPlatform();
        var transport = new BleFrameTransport(Options, platform, TimeProvider.System);
        var disposal = new ControlledBleOperation<bool>(default);
        var native = await Connect(transport, platform, new() { Disposal = disposal });
        using var cancellation = new CancellationTokenSource();
        var closing = transport.DisconnectAsync(cancellation.Token);
        cancellation.Cancel();
        var error = await Throws<OperationCanceledException>(() => closing);
        Check(error.CancellationToken == cancellation.Token && !transport.IsConnected);
        disposal.Complete(true);
        await transport.CleanupCompletion;
        await transport.DisposeAsync();
        Check(native.DisposeCount == 1);
    }

    private static async Task SlowPreparationCleanup()
    {
        var platform = new FakeBleGattOpenPlatform();
        var clock = new BleDiscoveryTestClock();
        var transport = new BleFrameTransport(Options, platform, clock);
        var disposal = new ControlledBleOperation<bool>(default);
        var native = new FakeBleGattSession
        {
            Capabilities = new(false, false, false, false, false), Disposal = disposal,
        };
        var connect = transport.ConnectAsync();
        (await platform.Opens.Reader.ReadAsync()).Operation.Complete(native);
        await clock.WaitForTimerAsync(Options.ShutdownTimeout);
        clock.Advance(Options.ShutdownTimeout);
        var failure = await Throws<MeshCoreBleTransportException>(() => connect);
        Check(failure.Reason == MeshCoreBleFailureReason.MissingService && native.DisposeCount == 1);
        var cleanup = transport.CleanupCompletion;
        Check(!cleanup.IsCompleted);
        disposal.Complete(true);
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        await transport.DisposeAsync();
        Check(native.DisposeCount == 1);
    }

    private static async Task SynchronousDisconnect()
    {
        var platform = new FakeBleGattOpenPlatform();
        await using var transport = new BleFrameTransport(Options, platform, TimeProvider.System);
        var native = await Connect(transport, platform);
        native.OnWrite = () => native.EmitDisconnect();
        var sending = transport.SendAsync(new byte[] { 1 }).AsTask();
        var operation = await native.Writes.Reader.ReadAsync();
        await Throws<MeshCoreBleTransportException>(() => sending);
        var cleanup = transport.CleanupCompletion;
        Check(!cleanup.IsCompleted && native.DisposeCount == 0);
        operation.Operation.Complete(true);
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        Check(native.DisposeCount == 1);
    }

    private static async Task LatePartialFailure()
    {
        var platform = new FakeBleGattOpenPlatform();
        var clock = new BleDiscoveryTestClock();
        var transport = new BleFrameTransport(Options, platform, clock);
        using var cancellation = new CancellationTokenSource();
        var opening = transport.ConnectAsync(cancellation.Token);
        var pending = await platform.Opens.Reader.ReadAsync();
        cancellation.Cancel();
        await Throws<OperationCanceledException>(() => opening);
        var failure = new IOException("Late native discovery failure");
        failure.Data["BleCleanupError"] = new IOException("Partial resource release failed");
        pending.Operation.Fail(failure);
        var error = await Throws<AggregateException>(() => transport.CleanupCompletion);
        Check(error.ToString().Contains("Partial resource release failed"));
        await Throws<AggregateException>(() => transport.DisposeAsync().AsTask());
    }

    private static async Task ClientDisposal()
    {
        var platform = new FakeBleGattOpenPlatform();
        var transport = new BleFrameTransport(Options, platform, TimeProvider.System);
        var client = new MeshCoreClient(transport, new MeshCoreClientOptions { AutoReceiveMessages = false });
        var connect = client.ConnectAsync();
        var native = new FakeBleGattSession();
        (await platform.Opens.Reader.ReadAsync()).Operation.Complete(native);
        (await native.Subscriptions.Reader.ReadAsync()).Complete(true);
        await connect;
        var start = client.StartAsync();
        var appStart = await native.Writes.Reader.ReadAsync();
        byte[] self = new byte[58]; self[0] = (byte)PacketType.SelfInfo;
        native.EmitNotification(self); appStart.Operation.Complete(true);
        await start;
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        client.PacketReceived += (_, _) => { blocked.TrySetResult(); release.Wait(); };
        try
        {
            native.EmitNotification(new byte[] { 0xFE });
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var time = client.GetDeviceTimeAsync();
            (await native.Writes.Reader.ReadAsync()).Operation.Complete(true);
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Throws<Exception>(() => time);
            await transport.CleanupCompletion;
            Check(native.DisposeCount == 1);
        }
        finally { release.Set(); }
    }
}
