using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Transport.Ble;

internal static class BleContractTests
{
    internal static (string Name, Func<Task> Run)[] Cases =>
    [
        ("BLE validates endpoint, bounded timeout and receive limits", Options),
        ("BLE retains typed failure and native diagnostics", Diagnostics),
        ("BLE fake controls discovery and independent open results", DiscoveryAndOpen),
        ("BLE fake controls subscription, writes, MTU and disconnect", GattOperations),
        ("BLE generation owns cleanup and rejects late open results", GenerationOwnership),
        ("BLE cancelled open completes late without reviving its generation", LateOpen),
        ("BLE cleanup failure is terminal and observed", CleanupFailure),
    ];

    private static BleMeshCoreTransportOptions Config => new() { DeviceId = "opaque:test-node" };
    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("BLE contract assertion failed.");
    }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task Options()
    {
        Config.Validate();
        Check(Config.MaximumFrameSize == ProtocolLimits.CurrentFirmwareMaxFrameSize);
        Check(Config.ReceiveQueueCapacity > 0 && Config.OpenTimeout > TimeSpan.Zero);
        Check(BleGattProfile.Service == Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E"));
        Check(BleGattProfile.AppToCompanion != BleGattProfile.CompanionToApp);
        foreach (var invalid in new[]
        {
            Config with { DeviceId = null! }, Config with { DeviceId = "" }, Config with { DeviceId = " " },
            Config with { OpenTimeout = TimeSpan.Zero }, Config with { OpenTimeout = Timeout.InfiniteTimeSpan },
            Config with { OpenTimeout = TimeSpan.MaxValue },
            Config with { OpenTimeout = TimeSpan.FromMilliseconds(uint.MaxValue) },
            Config with { ReceiveQueueCapacity = 0 }, Config with { ReceiveQueueCapacity = -1 },
            Config with { MaximumFrameSize = 0 }, Config with { MaximumFrameSize = -1 },
            Config with { MaximumFrameSize = ushort.MaxValue },
        })
            await Throws<ArgumentException>(() => { _ = new BleConnectionAttempt(invalid); return Task.CompletedTask; });
        // Endpoint is opaque: do not trim/normalize it as an address or device name.
        var opaque = Config with { DeviceId = " platform-specific ID " };
        opaque.Validate();
        Check(opaque.DeviceId == " platform-specific ID ");
    }

    private static async Task Diagnostics()
    {
        var native = new IOException("Synthetic platform failure.");
        foreach (var reason in Enum.GetValues<MeshCoreBleFailureReason>())
        {
            var error = new MeshCoreBleTransportException(reason, "BLE operation failed.", native,
                unchecked((int)0x80070005), 3, 5);
            Check(error is MeshCoreTransportException && error.Reason == reason);
            Check(ReferenceEquals(error.InnerException, native));
            Check(error.NativeHResult == error.HResult && error.HResult == unchecked((int)0x80070005));
            Check(error.NativeStatus == 3 && error.ProtocolError == 5);
        }
        var noNative = new MeshCoreBleTransportException(MeshCoreBleFailureReason.UnsupportedPlatform, "Unsupported.");
        Check(noNative.NativeHResult is null && noNative.NativeStatus is null && noNative.ProtocolError is null);
        await Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = new MeshCoreBleTransportException((MeshCoreBleFailureReason)int.MaxValue, "Invalid.");
            return Task.CompletedTask;
        });
    }

    private static async Task DiscoveryAndOpen()
    {
        var backend = new FakeBleBackend();
        using var cancelled = new CancellationTokenSource();
        var discovery = backend.DiscoverAsync(TimeSpan.FromSeconds(2), cancelled.Token);
        var pendingDiscovery = await backend.Discoveries.Reader.ReadAsync();
        Check(!discovery.IsCompleted && pendingDiscovery.Duration == TimeSpan.FromSeconds(2));
        var first = backend.OpenAsync("first", cancelled.Token);
        var second = backend.OpenAsync("second", CancellationToken.None);
        var firstOpen = await backend.Opens.Reader.ReadAsync();
        var secondOpen = await backend.Opens.Reader.ReadAsync();
        Check(firstOpen.DeviceId == "first" && secondOpen.DeviceId == "second");
        await using var session = new FakeBleGattSession();
        secondOpen.Operation.Complete(session);
        Check(ReferenceEquals(await second, session) && !first.IsCompleted);
        cancelled.Cancel();
        Check(!first.IsCompleted && firstOpen.Operation.CancellationToken.IsCancellationRequested);
        var error = new MeshCoreBleTransportException(MeshCoreBleFailureReason.DeviceUnreachable, "Unreachable.");
        firstOpen.Operation.Fail(error);
        Check(ReferenceEquals(await Throws<MeshCoreBleTransportException>(() => first), error));
        pendingDiscovery.Operation.Complete([
            new() { DeviceId = "a", Name = "Same name", IsPaired = true },
            new() { DeviceId = "b", Name = "Same name", AdvertisesCompanionService = true },
        ]);
        var devices = await discovery;
        Check(devices.Count == 2 && devices[0].DeviceId != devices[1].DeviceId);
        var cancelledScan = backend.DiscoverAsync(TimeSpan.FromSeconds(1), cancelled.Token);
        (await backend.Discoveries.Reader.ReadAsync()).Operation.Cancel();
        var cancellation = await Throws<OperationCanceledException>(() => cancelledScan);
        Check(cancellation.CancellationToken == cancelled.Token);
    }

    private static async Task GattOperations()
    {
        var session = new FakeBleGattSession { AutoCompleteUnsubscriptions = false };
        var received = new List<byte[]>();
        Exception? disconnected = null;
        ushort mtu = 0;
        session.NotificationReceived += frame => received.Add(frame.ToArray());
        session.Disconnected += error => disconnected = error;
        session.MaxPduSizeChanged += size => mtu = size;
        var callbacks = session.CaptureCallbacks();
        using var cancellation = new CancellationTokenSource();
        var subscribe = session.EnableNotificationsAsync(cancellation.Token);
        var pendingSubscribe = await session.Subscriptions.Reader.ReadAsync();
        session.EmitNotification(new byte[] { 0x83 }); // Can arrive before CCCD completion.
        Check(!subscribe.IsCompleted && received.Count == 1);
        pendingSubscribe.Complete(true);
        await subscribe;
        var bytes = new byte[] { 0x05 };
        var write = session.WriteAsync(bytes, cancellation.Token);
        var pendingWrite = await session.Writes.Reader.ReadAsync();
        bytes[0] = 0xFF;
        Check(pendingWrite.Frame.Span.SequenceEqual(new byte[] { 0x05 }));
        cancellation.Cancel();
        Check(!write.IsCompleted && pendingWrite.Operation.CancellationToken.IsCancellationRequested);
        session.EmitNotification(new byte[] { 9, 1, 0, 0, 0 });
        pendingWrite.Operation.Complete(true); // Late success is distinct from cancellation of the wait.
        await write;
        session.EmitMaxPduSize(179);
        Check(mtu == 179 && session.MaxPduSize == 179 && received.Count == 2);
        var error = new IOException("Synthetic disconnect.");
        session.EmitDisconnect(error);
        Check(ReferenceEquals(disconnected, error));
        var unsubscribe = session.DisableNotificationsAsync(CancellationToken.None);
        var pendingUnsubscribe = await session.Unsubscriptions.Reader.ReadAsync();
        pendingUnsubscribe.Complete(true);
        await unsubscribe;
        await session.DisposeAsync();
        session.EmitNotification(new byte[] { 0x83 });
        Check(received.Count == 2);
        callbacks.Notification?.Invoke(new byte[] { 0x83 });
        Check(received.Count == 3); // Fake can reproduce callbacks already dispatched before disposal.
    }

    private static async Task GenerationOwnership()
    {
        var first = new BleConnectionAttempt(Config);
        Check(first.State == BleConnectionState.Closed && !first.TryMarkReadyAfterSubscription());
        first.BeginOpening();
        Check(first.State == BleConnectionState.Opening && !first.TryMarkReadyAfterSubscription());
        var dispose = new ControlledBleOperation<bool>(CancellationToken.None);
        var session = new FakeBleGattSession { Disposal = dispose };
        Check(await first.AdoptSessionAsync(session));
        Check(first.TryMarkReadyAfterSubscription() && first.State == BleConnectionState.Ready);
        var failure = new IOException("Synthetic failure.");
        var close = first.CloseAsync(failure);
        Check(first.State == BleConnectionState.Closing && !close.IsCompleted);
        Check(ReferenceEquals(close, first.CloseAsync()));
        Check(session.DisposeCount == 1 && !first.TryMarkReadyAfterSubscription());
        dispose.Complete(true);
        await close;
        await first.DisposeAsync();
        Check(first.State == BleConnectionState.Faulted && ReferenceEquals(first.Error, failure));
        Check(session.DisposeCount == 1);
        var late = new FakeBleGattSession();
        Check(!await first.AdoptSessionAsync(late) && late.DisposeCount == 1);
        await Throws<InvalidOperationException>(() => { first.BeginOpening(); return Task.CompletedTask; });

        await using var second = new BleConnectionAttempt(Config);
        Check(second.Generation != first.Generation);
        second.BeginOpening();
        var nextSession = new FakeBleGattSession();
        Check(await second.AdoptSessionAsync(nextSession));
        // A stale callback checking its captured attempt cannot mark the new generation Ready.
        var staleReady = first.TryMarkReadyAfterSubscription;
        Check(!staleReady() && second.State == BleConnectionState.Opening);
        Check(second.TryMarkReadyAfterSubscription());
        await second.DisposeAsync();
        Check(second.State == BleConnectionState.Closed && nextSession.DisposeCount == 1);
    }

    private static async Task CleanupFailure()
    {
        var attempt = new BleConnectionAttempt(Config);
        attempt.BeginOpening();
        var disposal = new ControlledBleOperation<bool>(CancellationToken.None);
        var session = new FakeBleGattSession { Disposal = disposal };
        Check(await attempt.AdoptSessionAsync(session));
        var close = attempt.CloseAsync();
        var error = new IOException("Synthetic cleanup failure.");
        disposal.Fail(error);
        Check(ReferenceEquals(await Throws<IOException>(() => close), error));
        Check(attempt.State == BleConnectionState.Faulted && ReferenceEquals(attempt.Error, error));
        Check(ReferenceEquals(close, attempt.CloseAsync()) && session.DisposeCount == 1);
    }

    private static async Task LateOpen()
    {
        var backend = new FakeBleBackend();
        using var cancellation = new CancellationTokenSource();
        await using var abandoned = new BleConnectionAttempt(Config);
        abandoned.BeginOpening();
        var open = backend.OpenAsync(Config.DeviceId, cancellation.Token);
        var pending = await backend.Opens.Reader.ReadAsync();
        cancellation.Cancel();
        var cancelledWait = await Throws<OperationCanceledException>(() => open.WaitAsync(cancellation.Token));
        Check(cancelledWait.CancellationToken == cancellation.Token && !open.IsCompleted);
        await abandoned.CloseAsync(); // Closing never waits for native open to finish.

        await using var current = new BleConnectionAttempt(Config);
        current.BeginOpening();
        var currentSession = new FakeBleGattSession();
        Check(await current.AdoptSessionAsync(currentSession));
        var lateSession = new FakeBleGattSession();
        pending.Operation.Complete(lateSession);
        Check(!await abandoned.AdoptSessionAsync(await open));
        Check(lateSession.DisposeCount == 1 && currentSession.DisposeCount == 0);
        Check(abandoned.State == BleConnectionState.Closed && !abandoned.TryMarkReadyAfterSubscription());
        Check(current.State == BleConnectionState.Opening && current.TryMarkReadyAfterSubscription());
    }
}
