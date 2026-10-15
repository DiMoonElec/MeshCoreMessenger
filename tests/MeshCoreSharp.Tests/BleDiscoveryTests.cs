using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Transport.Ble;

internal static class BleDiscoveryTests
{
    internal static (string Name, Func<Task> Run)[] Cases =>
    [
        ("BLE discovery validates finite budgets and opaque saved endpoints", Options),
        ("BLE discovery merges advertisement and scan response by typed address", MergeAdvertisements),
        ("BLE discovery preserves paired and unavailable saved endpoints", SavedAndPaired),
        ("BLE discovery cancellation detaches callbacks before restart", CancelAndRestart),
        ("BLE discovery distinguishes adapter off, missing adapter and denied access", Availability),
        ("BLE discovery stops and disposes watchers on start/scan failures", WatcherFailures),
        ("BLE discovery returns empty results and omits unresolvable addresses", EmptyAndUnresolved),
        ("BLE discovery bounds native metadata and ignores late completions", MetadataTimeout),
    ];

    private static BleDiscoveryOptions Config => new()
    {
        ScanDuration = TimeSpan.FromSeconds(3), LookupTimeout = TimeSpan.FromSeconds(7),
    };
    private static BleAddress Address(ulong value = 1, BleAddressKind kind = BleAddressKind.Public) => new(value, kind);
    private static BleDeviceInfo Device(string id, string? name = null) => new() { DeviceId = id, Name = name };
    private static MeshCoreBleTransportException Failure(MeshCoreBleFailureReason reason) => new(reason, "Synthetic discovery failure.");
    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("BLE discovery assertion failed.");
    }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static void Closed(FakeBleAdvertisementScanner scanner) =>
        Check(scanner.StopCount == 1 && scanner.DisposeCount == 1 && !scanner.HasHandlers);
    private static async Task FinishScan(BleDiscoveryTestClock clock)
    {
        await clock.WaitForTimerAsync(Config.ScanDuration);
        clock.Advance(Config.ScanDuration);
    }

    private static async Task Options()
    {
        var platform = new FakeBleDiscoveryPlatform();
        var discovery = new BleDeviceDiscovery(platform, new BleDiscoveryTestClock());
        foreach (var invalid in new[]
        {
            Config with { ScanDuration = TimeSpan.Zero }, Config with { ScanDuration = Timeout.InfiniteTimeSpan },
            Config with { LookupTimeout = TimeSpan.Zero }, Config with { LookupTimeout = TimeSpan.MaxValue },
            Config with { KnownDevices = null! }, Config with { KnownDevices = [null!] },
            Config with { KnownDevices = [Device(" ")] },
        })
            await Throws<ArgumentException>(() => discovery.FindAsync(invalid));
        Check(platform.CheckCount == 0 && !platform.Scanners.Reader.TryRead(out _));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Throws<OperationCanceledException>(() => discovery.FindAsync(Config, cancelled.Token));
        Check(platform.CheckCount == 0);
#if !WINDOWS
        var unsupported = await Throws<MeshCoreBleTransportException>(() => new BleDeviceDiscovery().FindAsync(Config));
        Check(unsupported.Reason == MeshCoreBleFailureReason.UnsupportedPlatform);
#endif
    }

    private static async Task MergeAdvertisements()
    {
        var platform = new FakeBleDiscoveryPlatform();
        platform.Endpoints[Address()] = Device("windows-public");
        platform.Endpoints[Address(kind: BleAddressKind.Random)] = Device("windows-random");
        var clock = new BleDiscoveryTestClock();
        var scan = new BleDeviceDiscovery(platform, clock).FindAsync(Config);
        var scanner = await platform.Scanners.Reader.ReadAsync();
        scanner.Emit(new(Address(), "Same name", false));
        scanner.Emit(new(Address(), null, true));
        scanner.Emit(new(Address(), "", false)); // Missing UUID in a later packet must not clear it.
        scanner.Emit(new(Address(kind: BleAddressKind.Random), "Same name", true));
        scanner.Emit(new(Address(2), "MeshCore-looking name", false)); // Name alone is not evidence.
        await FinishScan(clock);
        var found = await scan;
        Check(found.Count == 2 && found.All(device => device.Name == "Same name" && device.AdvertisesCompanionService));
        Check(found.Select(device => device.DeviceId).Distinct().Count() == 2);
        Check(platform.Resolved.Count == 2 && platform.Resolved.Contains(Address())
            && platform.Resolved.Contains(Address(kind: BleAddressKind.Random)));
        Closed(scanner);
    }

    private static async Task SavedAndPaired()
    {
        foreach (var includePaired in new[] { true, false })
        {
            var platform = new FakeBleDiscoveryPlatform { Paired = [Device("paired", "New name") with { IsPaired = true }] };
            var clock = new BleDiscoveryTestClock();
            var scan = new BleDeviceDiscovery(platform, clock).FindAsync(Config with
            {
                IncludePairedDevices = includePaired,
                KnownDevices = [Device("offline", "Saved") with { IsPaired = true, AdvertisesCompanionService = true },
                    Device("paired", "Old name")],
            });
            var scanner = await platform.Scanners.Reader.ReadAsync();
            await FinishScan(clock);
            var found = await scan;
            Check(found.Count == 2 && found.All(device => !device.AdvertisesCompanionService));
            Check(found.Single(device => device.DeviceId == "offline") is { Name: "Saved", IsPaired: false });
            var paired = found.Single(device => device.DeviceId == "paired");
            Check(paired.IsPaired == includePaired && paired.Name == (includePaired ? "New name" : "Old name"));
            Check(platform.PairedReads == (includePaired ? 1 : 0));
            Closed(scanner);
        }
    }

    private static async Task CancelAndRestart()
    {
        var platform = new FakeBleDiscoveryPlatform();
        platform.Endpoints[Address()] = Device("current");
        platform.Endpoints[Address(9)] = Device("stale");
        var clock = new BleDiscoveryTestClock();
        var discovery = new BleDeviceDiscovery(platform, clock);
        using var cancellation = new CancellationTokenSource();
        var abandoned = discovery.FindAsync(Config, cancellation.Token);
        var old = await platform.Scanners.Reader.ReadAsync();
        await clock.WaitForTimerAsync(Config.ScanDuration);
        var callbacks = old.CaptureCallbacks();
        cancellation.Cancel();
        var error = await Throws<OperationCanceledException>(() => abandoned);
        Check(error.CancellationToken == cancellation.Token);
        Closed(old);
        var current = discovery.FindAsync(Config);
        var scanner = await platform.Scanners.Reader.ReadAsync();
        callbacks.Received?.Invoke(new(Address(9), "stale", true));
        callbacks.Stopped?.Invoke(Failure(MeshCoreBleFailureReason.RadioOff));
        scanner.Emit(new(Address(), "current", true));
        await FinishScan(clock);
        Check((await current).Single().DeviceId == "current");
        Closed(scanner);
    }

    private static async Task Availability()
    {
        foreach (var reason in new[] { MeshCoreBleFailureReason.RadioOff, MeshCoreBleFailureReason.AdapterUnavailable,
            MeshCoreBleFailureReason.AccessDenied })
        {
            var failure = Failure(reason);
            var platform = new FakeBleDiscoveryPlatform { AvailabilityError = failure };
            var error = await Throws<MeshCoreBleTransportException>(() =>
                new BleDeviceDiscovery(platform, new BleDiscoveryTestClock()).FindAsync(Config));
            Check(ReferenceEquals(error, failure) && !platform.Scanners.Reader.TryRead(out _));
        }
    }

    private static async Task WatcherFailures()
    {
        var failedStart = new FakeBleDiscoveryPlatform { StartError = Failure(MeshCoreBleFailureReason.AccessDenied) };
        await Throws<MeshCoreBleTransportException>(() => new BleDeviceDiscovery(failedStart,
            new BleDiscoveryTestClock()).FindAsync(Config));
        Closed(await failedStart.Scanners.Reader.ReadAsync());
        foreach (var failure in new Exception?[] { Failure(MeshCoreBleFailureReason.RadioOff), null })
        {
            var platform = new FakeBleDiscoveryPlatform();
            var clock = new BleDiscoveryTestClock();
            var scan = new BleDeviceDiscovery(platform, clock).FindAsync(Config);
            var scanner = await platform.Scanners.Reader.ReadAsync();
            await clock.WaitForTimerAsync(Config.ScanDuration);
            scanner.Fail(failure);
            var error = await Throws<MeshCoreBleTransportException>(() => scan);
            Check(error.Reason == (failure is null ? MeshCoreBleFailureReason.TransportFailure : MeshCoreBleFailureReason.RadioOff));
            Closed(scanner);
        }
    }

    private static async Task EmptyAndUnresolved()
    {
        foreach (var advertise in new[] { false, true })
        {
            var platform = new FakeBleDiscoveryPlatform();
            var clock = new BleDiscoveryTestClock();
            var scan = new BleDeviceDiscovery(platform, clock).FindAsync(Config);
            var scanner = await platform.Scanners.Reader.ReadAsync();
            if (advertise) scanner.Emit(new(Address(), "Unresolvable", true));
            await FinishScan(clock);
            Check((await scan).Count == 0);
            Check(platform.Resolved.Count == (advertise ? 1 : 0));
            Closed(scanner);
        }
    }

    private static async Task MetadataTimeout()
    {
        var pendingCheck = new ControlledBleOperation<bool>(CancellationToken.None);
        var unavailable = new FakeBleDiscoveryPlatform { Check = _ => pendingCheck.Task };
        var clock = new BleDiscoveryTestClock();
        var starting = new BleDeviceDiscovery(unavailable, clock).FindAsync(Config);
        await clock.WaitForTimerAsync(Config.LookupTimeout);
        clock.Advance(Config.LookupTimeout);
        Check((await Throws<MeshCoreBleTransportException>(() => starting)).Reason == MeshCoreBleFailureReason.TransportFailure);
        Check(!unavailable.Scanners.Reader.TryRead(out _));
        pendingCheck.Fail(new IOException("Late check failure.")); // Must be observed even after timeout.

        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new ControlledBleOperation<BleDeviceInfo?>(CancellationToken.None);
        var platform = new FakeBleDiscoveryPlatform { Resolve = (_, _) => { resolving.TrySetResult(); return pending.Task; } };
        clock = new BleDiscoveryTestClock();
        var discovery = new BleDeviceDiscovery(platform, clock);
        var scan = discovery.FindAsync(Config);
        var scanner = await platform.Scanners.Reader.ReadAsync();
        scanner.Emit(new(Address(), "pending", true));
        await FinishScan(clock);
        await resolving.Task;
        await clock.WaitForTimerAsync(Config.LookupTimeout);
        clock.Advance(Config.LookupTimeout);
        await Throws<MeshCoreBleTransportException>(() => scan);
        Closed(scanner);
        pending.Complete(Device("late"));
        var next = discovery.FindAsync(Config);
        var nextScanner = await platform.Scanners.Reader.ReadAsync();
        await FinishScan(clock);
        Check((await next).Count == 0);
        Closed(nextScanner);
    }
}
