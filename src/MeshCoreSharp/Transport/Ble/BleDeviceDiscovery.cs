using MeshCoreSharp.Exceptions;

namespace MeshCoreSharp.Transport.Ble;

/// <summary>Discovers candidate Companion endpoints. Names and NUS advertisements do not verify node identity.</summary>
public sealed class BleDeviceDiscovery
{
    private readonly IBleDiscoveryPlatform _platform;
    private readonly TimeProvider _time;

    public BleDeviceDiscovery() : this(CreatePlatform(), TimeProvider.System) { }

    internal BleDeviceDiscovery(IBleDiscoveryPlatform platform, TimeProvider time)
    {
        _platform = platform;
        _time = time;
    }

    public async Task<IReadOnlyList<BleDeviceInfo>> FindAsync(
        BleDiscoveryOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var devices = new Dictionary<string, BleDeviceInfo>(StringComparer.Ordinal);
        foreach (var device in options.KnownDevices.ToArray())
            Merge(devices, device with { IsPaired = false, AdvertisesCompanionService = false });
        await LookupAsync(async token =>
        {
            await Observe(_platform.CheckAvailabilityAsync(token)).WaitAsync(token).ConfigureAwait(false);
            return true;
        }, options.LookupTimeout, "adapter availability", cancellationToken).ConfigureAwait(false);

        var advertisements = new Dictionary<BleAddress, BleAdvertisement>();
        var sync = new object();
        var accepting = true;
        var stopped = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var scanner = _platform.CreateScanner())
        {
            void OnReceived(BleAdvertisement update)
            {
                lock (sync)
                {
                    if (!accepting) return;
                    if (advertisements.TryGetValue(update.Address, out var previous))
                        update = update with
                        {
                            Name = string.IsNullOrEmpty(update.Name) ? previous.Name : update.Name,
                            HasCompanionService = update.HasCompanionService || previous.HasCompanionService,
                        };
                    advertisements[update.Address] = update;
                }
            }
            void OnStopped(Exception? error)
            {
                lock (sync)
                {
                    if (accepting)
                        stopped.TrySetResult(error ?? new MeshCoreBleTransportException(
                            MeshCoreBleFailureReason.TransportFailure, "BLE scan stopped unexpectedly."));
                }
            }
            scanner.Received += OnReceived;
            scanner.Stopped += OnStopped;
            using var scanLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanner.Start();
                var elapsed = Task.Delay(options.ScanDuration, _time, scanLifetime.Token);
                await Task.WhenAny(elapsed, stopped.Task).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (stopped.Task.IsCompleted)
                    throw await stopped.Task.ConfigureAwait(false);
                await elapsed.ConfigureAwait(false);
            }
            finally
            {
                lock (sync) accepting = false;
                scanner.Received -= OnReceived;
                scanner.Stopped -= OnStopped;
                scanLifetime.Cancel();
                scanner.Stop();
            }
        }

        // Scanner and callbacks are already detached. Late results cannot mutate the next invocation.
        var candidates = advertisements.Values.Where(item => item.HasCompanionService).ToArray();
        await LookupAsync(async token =>
        {
            var paired = options.IncludePairedDevices
                ? Observe(_platform.GetPairedDevicesAsync(token)).WaitAsync(token)
                : Task.FromResult<IReadOnlyList<BleDeviceInfo>>([]);
            var resolving = Task.WhenAll(candidates.Select(async advertisement =>
            {
                var device = await Observe(_platform.ResolveAsync(advertisement.Address, token))
                    .WaitAsync(token).ConfigureAwait(false);
                return device is null ? null : device with
                {
                    Name = string.IsNullOrEmpty(advertisement.Name) ? device.Name : advertisement.Name,
                    AdvertisesCompanionService = true,
                };
            }));
            await Task.WhenAll(paired, resolving).ConfigureAwait(false);
            var resolved = await resolving.ConfigureAwait(false);
            var pairedDevices = await paired.ConfigureAwait(false);
            foreach (var device in pairedDevices) Merge(devices, device);
            foreach (var device in resolved)
                if (device is not null) Merge(devices, device);
            return true;
        }, options.LookupTimeout, "device metadata", cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return devices.Values.OrderByDescending(device => device.AdvertisesCompanionService)
            .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.DeviceId, StringComparer.Ordinal).ToArray();
    }

    private async Task<T> LookupAsync<T>(Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout, string operationName, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(timeout, _time);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var task = operation(lifetime.Token);
            // Some WinRT metadata operations may complete after cancellation of the wait.
            return await Observe(task).WaitAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new MeshCoreBleTransportException(MeshCoreBleFailureReason.TransportFailure,
                $"BLE discovery {operationName} lookup timed out.", error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static TTask Observe<TTask>(TTask task) where TTask : Task
    {
        _ = task.ContinueWith(failed => { _ = failed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private static void Merge(Dictionary<string, BleDeviceInfo> devices, BleDeviceInfo update)
    {
        if (devices.TryGetValue(update.DeviceId, out var previous))
            update = update with
            {
                Name = string.IsNullOrEmpty(update.Name) ? previous.Name : update.Name,
                IsPaired = update.IsPaired || previous.IsPaired,
                AdvertisesCompanionService = update.AdvertisesCompanionService || previous.AdvertisesCompanionService,
            };
        devices[update.DeviceId] = update;
    }

    private static IBleDiscoveryPlatform CreatePlatform()
    {
#if WINDOWS
        return new Windows.WindowsBleDiscoveryPlatform();
#else
        return new UnsupportedDiscoveryPlatform();
#endif
    }

#if !WINDOWS
    private sealed class UnsupportedDiscoveryPlatform : IBleDiscoveryPlatform
    {
        private static MeshCoreBleTransportException Error() => new(
            MeshCoreBleFailureReason.UnsupportedPlatform, "BLE discovery requires the Windows library target.");
        public Task CheckAvailabilityAsync(CancellationToken cancellationToken) => Task.FromException(Error());
        public IBleAdvertisementScanner CreateScanner() => throw Error();
        public Task<IReadOnlyList<BleDeviceInfo>> GetPairedDevicesAsync(CancellationToken cancellationToken) => throw Error();
        public Task<BleDeviceInfo?> ResolveAsync(BleAddress address, CancellationToken cancellationToken) => throw Error();
    }
#endif
}
