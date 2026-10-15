using System.Threading.Channels;
using MeshCoreSharp.Transport.Ble;

internal sealed class FakeBleDiscoveryPlatform : IBleDiscoveryPlatform
{
    internal Exception? AvailabilityError { get; init; }
    internal Func<CancellationToken, Task>? Check { get; init; }
    internal Func<BleAddress, CancellationToken, Task<BleDeviceInfo?>>? Resolve { get; init; }
    internal IReadOnlyList<BleDeviceInfo> Paired { get; init; } = [];
    internal Dictionary<BleAddress, BleDeviceInfo> Endpoints { get; } = [];
    internal Channel<FakeBleAdvertisementScanner> Scanners { get; } = Channel.CreateUnbounded<FakeBleAdvertisementScanner>();
    internal List<BleAddress> Resolved { get; } = [];
    internal int CheckCount;
    internal int PairedReads;
    internal Exception? StartError { get; init; }

    public Task CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        CheckCount++;
        return Check?.Invoke(cancellationToken)
            ?? (AvailabilityError is null ? Task.CompletedTask : Task.FromException(AvailabilityError));
    }

    public IBleAdvertisementScanner CreateScanner()
    {
        var scanner = new FakeBleAdvertisementScanner { StartError = StartError };
        Scanners.Writer.TryWrite(scanner);
        return scanner;
    }

    public Task<IReadOnlyList<BleDeviceInfo>> GetPairedDevicesAsync(CancellationToken cancellationToken)
    {
        PairedReads++;
        return Task.FromResult(Paired);
    }

    public Task<BleDeviceInfo?> ResolveAsync(BleAddress address, CancellationToken cancellationToken)
    {
        Resolved.Add(address);
        return Resolve?.Invoke(address, cancellationToken)
            ?? Task.FromResult(Endpoints.GetValueOrDefault(address));
    }
}

internal sealed class FakeBleAdvertisementScanner : IBleAdvertisementScanner
{
    public event Action<BleAdvertisement>? Received;
    public event Action<Exception?>? Stopped;
    internal Exception? StartError { get; init; }
    internal int StopCount;
    internal int DisposeCount;
    internal bool HasHandlers => Received is not null || Stopped is not null;
    public void Start() { if (StartError is not null) throw StartError; }
    public void Stop() => StopCount++;
    public void Dispose() => DisposeCount++;
    internal void Emit(BleAdvertisement advertisement) => Received?.Invoke(advertisement);
    internal void Fail(Exception? error) => Stopped?.Invoke(error);
    internal (Action<BleAdvertisement>? Received, Action<Exception?>? Stopped) CaptureCallbacks() => (Received, Stopped);
}

internal sealed class BleDiscoveryTestClock : TimeProvider
{
    private readonly ManualTimeProvider _clock = new();
    private readonly Channel<TimeSpan> _timers = Channel.CreateUnbounded<TimeSpan>();
    public override long TimestampFrequency => _clock.TimestampFrequency;
    public override long GetTimestamp() => _clock.GetTimestamp();
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = _clock.CreateTimer(callback, state, dueTime, period);
        _timers.Writer.TryWrite(dueTime);
        return timer;
    }
    internal void Advance(TimeSpan duration) => _clock.Advance(duration);
    internal async Task WaitForTimerAsync(TimeSpan duration)
    {
        while (await _timers.Reader.ReadAsync() != duration) { }
    }
}
