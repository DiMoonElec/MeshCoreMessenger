using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Transport.Ble;

internal static class BleReceiveTests
{
    internal static (string Name, Func<Task> Run)[] Cases =>
    [
        ("BLE RX retains the first notification inside CCCD before Ready", DuringSubscription),
        ("BLE RX copies memory and preserves push and unknown frame order", CopiesAndOrder),
        ("BLE RX queue overflow faults explicitly without blocking callbacks", Overflow),
        ("BLE RX invalid values terminate locally with diagnostics", InvalidValues),
        ("BLE RX consumer cancellation does not close a healthy session", CancelReceive),
        ("BLE RX failed and cancelled CCCD cannot make a session Ready", SubscriptionFailure),
        ("BLE RX disconnect and MTU drop wake pending readers", LinkFailure),
        ("BLE RX late callbacks from a closed generation cannot reach the next one", OldGeneration),
    ];

    internal static BleMeshCoreTransportOptions Options(int capacity = 8) => new()
    {
        DeviceId = "fake:selected", ReceiveQueueCapacity = capacity,
    };
    internal static void Check(bool value)
    {
        if (!value) throw new InvalidOperationException("BLE RX assertion failed.");
    }
    internal static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    internal static async Task<(BleReceiveSession Receiver, FakeBleGattSession Native)> Prepare(int capacity = 8)
    {
        var options = Options(capacity);
        var native = new FakeBleGattSession();
        var attempt = new BleConnectionAttempt(options);
        attempt.BeginOpening();
        await attempt.AdoptSessionAsync(native);
        return (new BleReceiveSession(new(attempt, native), options), native);
    }
    internal static async Task<(BleReceiveSession Receiver, FakeBleGattSession Native)> Ready(int capacity = 8)
    {
        var pair = await Prepare(capacity);
        var start = pair.Receiver.StartAsync(default);
        (await pair.Native.Subscriptions.Reader.ReadAsync()).Complete(true);
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        return pair;
    }

    private static async Task DuringSubscription()
    {
        var (receiver, native) = await Prepare();
        await using var owner = receiver;
        var start = receiver.StartAsync(default);
        var subscription = await native.Subscriptions.Reader.ReadAsync();
        Check(!receiver.IsConnected);
        native.EmitNotification(new byte[] { 0x80 });
        subscription.Complete(true);
        await start;
        Check(receiver.IsConnected && !native.Writes.Reader.TryRead(out _));
        await using var reader = receiver.ReceiveAsync().GetAsyncEnumerator();
        Check(await reader.MoveNextAsync() && reader.Current.Span.SequenceEqual(new byte[] { 0x80 }));
    }

    private static async Task CopiesAndOrder()
    {
        var (receiver, native) = await Ready();
        await using var owner = receiver;
        byte[] buffer = [0x80, 1];
        native.EmitNotification(buffer);
        buffer[1] = 99;
        native.EmitNotification(new byte[] { 0 });
        native.EmitNotification(new byte[] { 0xFE, 7 });
        native.EmitNotification(new byte[] { 0x81 });
        await using var reader = receiver.ReceiveAsync().GetAsyncEnumerator();
        foreach (var expected in new byte[][] { [0x80, 1], [0], [0xFE, 7], [0x81] })
            Check(await reader.MoveNextAsync() && reader.Current.Span.SequenceEqual(expected));
    }

    private static async Task Overflow()
    {
        var (receiver, native) = await Ready(1);
        await using var owner = receiver;
        native.EmitNotification(new byte[] { 0x80 });
        native.EmitNotification(new byte[] { 0x81 });
        Check(!receiver.IsConnected);
        await using var reader = receiver.ReceiveAsync().GetAsyncEnumerator();
        Check(await reader.MoveNextAsync() && reader.Current.Span[0] == 0x80);
        var error = await Throws<MeshCoreBleTransportException>(async () => await reader.MoveNextAsync());
        Check(error.Reason == MeshCoreBleFailureReason.ReceiveQueueOverflow);
    }

    private static async Task InvalidValues()
    {
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[Options().MaximumFrameSize + 1] })
        {
            var (receiver, native) = await Ready();
            await using var owner = receiver;
            native.EmitNotification(bytes);
            await using var reader = receiver.ReceiveAsync().GetAsyncEnumerator();
            var error = await Throws<MeshCoreBleTransportException>(async () => await reader.MoveNextAsync());
            Check(error.Reason == MeshCoreBleFailureReason.InvalidFrame && !receiver.IsConnected);
        }
    }

    private static async Task CancelReceive()
    {
        var (receiver, native) = await Ready();
        await using var owner = receiver;
        using var cancel = new CancellationTokenSource();
        await using (var reader = receiver.ReceiveAsync(cancel.Token).GetAsyncEnumerator())
        {
            var read = reader.MoveNextAsync().AsTask();
            cancel.Cancel();
            var error = await Throws<OperationCanceledException>(() => read);
            Check(error.CancellationToken == cancel.Token && receiver.IsConnected);
        }
        native.EmitNotification(new byte[] { 0 });
        await using var next = receiver.ReceiveAsync().GetAsyncEnumerator();
        Check(await next.MoveNextAsync());
    }

    private static async Task SubscriptionFailure()
    {
        foreach (var cancel in new[] { false, true })
        {
            var (receiver, native) = await Prepare();
            await using var owner = receiver;
            using var cancellation = new CancellationTokenSource();
            var start = receiver.StartAsync(cancellation.Token);
            var pending = await native.Subscriptions.Reader.ReadAsync();
            if (cancel)
            {
                cancellation.Cancel();
                await Throws<OperationCanceledException>(() => start);
                pending.Complete(true); // Late CCCD success cannot revive this generation.
            }
            else
            {
                pending.Fail(new MeshCoreBleTransportException(MeshCoreBleFailureReason.AccessDenied,
                    "CCCD denied", nativeStatus: 3));
                var error = await Throws<MeshCoreBleTransportException>(() => start);
                Check(error.NativeStatus == 3);
            }
            Check(!receiver.IsConnected);
        }
    }

    private static async Task LinkFailure()
    {
        foreach (var mtu in new[] { false, true })
        {
            var (receiver, native) = await Ready();
            await using var owner = receiver;
            await using var reader = receiver.ReceiveAsync().GetAsyncEnumerator();
            var read = reader.MoveNextAsync().AsTask();
            if (mtu) native.EmitMaxPduSize(0); else native.EmitDisconnect();
            var error = await Throws<MeshCoreBleTransportException>(() => read);
            Check(error.Reason == (mtu ? MeshCoreBleFailureReason.InsufficientMtu : MeshCoreBleFailureReason.DeviceUnreachable));
        }
        var (opening, pendingNative) = await Prepare();
        await using var openingOwner = opening;
        var start = opening.StartAsync(default);
        var pendingSubscription = await pendingNative.Subscriptions.Reader.ReadAsync();
        pendingNative.EmitDisconnect();
        await Throws<MeshCoreBleTransportException>(() => start);
        pendingSubscription.Complete(true);
        Check(!opening.IsConnected);
    }

    private static async Task OldGeneration()
    {
        var (old, oldNative) = await Ready();
        var retained = oldNative.CaptureCallbacks();
        await old.DisposeAsync();
        var (next, nextNative) = await Ready();
        await using var owner = next;
        retained.Notification?.Invoke(new byte[] { 0xFF });
        retained.Disconnect?.Invoke(null);
        retained.Mtu?.Invoke(23);
        nextNative.EmitNotification(new byte[] { 0 });
        await using var reader = next.ReceiveAsync().GetAsyncEnumerator();
        Check(next.IsConnected && await reader.MoveNextAsync() && reader.Current.Span[0] == 0);
        await old.DisposeAsync();
        Check(oldNative.DisposeCount == 1);
    }
}
