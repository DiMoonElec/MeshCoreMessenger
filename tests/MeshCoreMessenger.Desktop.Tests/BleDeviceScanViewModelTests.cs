using Microsoft.Extensions.Logging.Abstractions;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreSharp.Exceptions;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class BleDeviceScanViewModelTests
{
    [Fact]
    public async Task ScanIsExplicitPreservesSavedEndpointAndDoesNotChooseFirstResult()
    {
        var context = new Context();
        Assert.Equal(BleScanState.Idle, context.Model.State);
        Assert.Empty(context.Catalog.Calls);
        await context.Model.LoadEndpointAsync("saved", "Saved offline");
        Assert.False(context.Model.SelectedDevice!.IsFound);
        var scan = context.Model.RefreshAsync(Token);
        Assert.Equal(BleScanState.Scanning, context.Model.State);
        context.Catalog.Complete(0, Device("first", "Same"), Device("second", "Same"));
        await scan.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal("saved", context.Model.SelectedDevice.DeviceId);
        Assert.False(context.Model.SelectedDevice.IsFound);
        Assert.Equal(3, context.Model.Devices.Count);
        var same = context.Model.Devices.Where(device => device.Name == "Same").ToArray();
        Assert.NotEqual(same[0].EndpointHint, same[1].EndpointHint);
        await context.Model.LoadEndpointAsync(null, null);
        var next = context.Model.RefreshAsync(Token);
        context.Catalog.Complete(1, Device("first", "Same"));
        await next.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Null(context.Model.SelectedDevice);
        Assert.Single(context.Model.Devices);
        Assert.Equal(BleScanState.Completed, context.Model.State);
    }

    [Fact]
    public async Task RefreshAndCancelIgnoreLateResultsEvenWhenCatalogIgnoresCancellation()
    {
        var context = new Context();
        var first = context.Model.RefreshAsync(Token);
        var second = context.Model.RefreshAsync(Token);
        Assert.True(context.Catalog.Calls[0].Token.IsCancellationRequested);
        await first.WaitAsync(TimeSpan.FromSeconds(5), Token);
        context.Catalog.Complete(1, Device("new", "New"));
        await second.WaitAsync(TimeSpan.FromSeconds(5), Token);
        context.Catalog.Complete(0, Device("old", "Old"));
        Assert.Equal("new", Assert.Single(context.Model.Devices).DeviceId);
        var third = context.Model.RefreshAsync(Token);
        await context.Model.CancelAsync();
        await third.WaitAsync(TimeSpan.FromSeconds(5), Token);
        context.Catalog.Calls[2].Completion.SetException(new IOException("late error"));
        Assert.Equal(BleScanState.Idle, context.Model.State);
        Assert.Null(context.Model.ErrorMessage);
        Assert.Equal("new", Assert.Single(context.Model.Devices).DeviceId);
    }

    [Fact]
    public async Task QueuedOldUiPublicationCannotOverwriteNewGeneration()
    {
        var context = new Context(queued: true);
        var old = context.Model.RefreshAsync(Token);
        context.Dispatcher.RunNext();
        await WaitUntilAsync(() => context.Catalog.Calls.Count == 1);
        var next = context.Model.RefreshAsync(Token); // Queue the next start before old result publication.
        context.Catalog.Complete(0, Device("stale", "Old"));
        await WaitUntilAsync(() => context.Dispatcher.PendingCount >= 2);
        await PumpUntilAsync(context.Dispatcher, () => context.Catalog.Calls.Count == 2);
        context.Catalog.Complete(1, Device("current", "New"));
        await PumpUntilAsync(context.Dispatcher, () => old.IsCompleted && next.IsCompleted);
        await Task.WhenAll(old, next);
        Assert.Equal("current", Assert.Single(context.Model.Devices).DeviceId);
        Assert.DoesNotContain("stale", context.PublishedIds);
        Assert.Equal(BleScanState.Completed, context.Model.State);
    }

    [Theory]
    [InlineData(MeshCoreBleFailureReason.AdapterUnavailable)]
    [InlineData(MeshCoreBleFailureReason.RadioOff)]
    [InlineData(MeshCoreBleFailureReason.AccessDenied)]
    public async Task ScanFailureKeepsSelectionAndHidesNativeErrorDetails(MeshCoreBleFailureReason reason)
    {
        var context = new Context();
        await context.Model.LoadEndpointAsync("saved", "Offline");
        var scan = context.Model.RefreshAsync(Token);
        context.Catalog.Calls[0].Completion.SetException(new MeshCoreBleTransportException(reason, "HRESULT 0xDEADBEEF"));
        await scan.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(BleScanState.Error, context.Model.State);
        Assert.Equal("saved", context.Model.SelectedDevice!.DeviceId);
        Assert.False(string.IsNullOrWhiteSpace(context.Model.ErrorMessage));
        Assert.DoesNotContain("HRESULT", context.Model.ErrorMessage);
    }

    [Fact]
    public async Task SettingsLaunchCancelsScanWithoutStartingAnotherAndShutdownSuppressesLateWork()
    {
        var context = new Context();
        var first = context.Model.RefreshAsync(Token);
        await context.Model.OpenBluetoothSettingsCommand.ExecuteAsync(null);
        await first.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(1, context.Settings.Count);
        Assert.Single(context.Catalog.Calls);
        Assert.Equal(BleScanState.Idle, context.Model.State);
        var next = context.Model.RefreshAsync(Token);
        await context.Model.StopAsync();
        await next.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(context.Catalog.Calls[1].Token.IsCancellationRequested);
        context.Catalog.Complete(0, Device("late-1", "Late"));
        context.Catalog.Complete(1, Device("late-2", "Late"));
        await context.Model.RefreshAsync(Token);
        Assert.Equal(2, context.Catalog.Calls.Count);
        Assert.Empty(context.Model.Devices);
        Assert.False(context.Model.ScanCommand.CanExecute(null));
        Assert.False(context.Model.OpenBluetoothSettingsCommand.CanExecute(null));
    }

    [Fact]
    public async Task UnsupportedPlatformStillShowsSavedEndpointButCannotScanOrLaunchSettings()
    {
        var context = new Context(); context.Catalog.IsSupported = false;
        await context.Model.LoadEndpointAsync("saved", "Offline");
        await context.Model.RefreshAsync(Token);
        Assert.Empty(context.Catalog.Calls);
        Assert.False(context.Model.ScanCommand.CanExecute(null));
        Assert.False(context.Model.OpenBluetoothSettingsCommand.CanExecute(null));
        Assert.Equal("saved", Assert.Single(context.Model.Devices).DeviceId);
    }

    private static BleDeviceChoice Device(string id, string name) => new(id, name, true, true, true);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(1, timeout.Token);
    }
    private static async Task PumpUntilAsync(ControlledDispatcher dispatcher, Func<bool> ready)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!ready())
        {
            if (dispatcher.PendingCount > 0) dispatcher.RunNext();
            else await Task.Delay(1, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
        }
    }

    private sealed class Context
    {
        public Context(bool queued = false)
        {
            Dispatcher = new() { AutoRun = !queued };
            Model = new(Catalog, Dispatcher, Settings, NullLogger<BleDeviceScanViewModel>.Instance);
            Model.Devices.CollectionChanged += (_, _) =>
            {
                Assert.True(Dispatcher.IsInvoking);
                PublishedIds.AddRange(Model.Devices.Select(device => device.DeviceId));
            };
            Model.PropertyChanged += (_, _) => Assert.True(Dispatcher.IsInvoking);
        }
        public Catalog Catalog { get; } = new();
        public Settings Settings { get; } = new();
        public ControlledDispatcher Dispatcher { get; }
        public BleDeviceScanViewModel Model { get; }
        public List<string> PublishedIds { get; } = [];
    }
    private sealed class Catalog : IBleDeviceCatalog
    {
        public bool IsSupported { get; set; } = true;
        public List<(CancellationToken Token, TaskCompletionSource<IReadOnlyList<BleDeviceChoice>> Completion)> Calls { get; } = [];
        public Task<IReadOnlyList<BleDeviceChoice>> FindAsync(CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<IReadOnlyList<BleDeviceChoice>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add((cancellationToken, completion));
            return completion.Task;
        }
        public void Complete(int index, params BleDeviceChoice[] devices) => Calls[index].Completion.SetResult(devices);
    }
    private sealed class Settings : IBluetoothSettingsLauncher
    {
        public int Count { get; private set; }
        public void Open() => Count++;
    }
    private sealed class ControlledDispatcher : IUiDispatcher
    {
        private readonly Queue<(Action Action, TaskCompletionSource Completion, CancellationToken Token)> _queue = [];
        private readonly object _gate = new();
        public bool AutoRun { get; init; }
        private int _depth;
        public bool IsInvoking => _depth > 0;
        public int PendingCount { get { lock (_gate) return _queue.Count; } }
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (AutoRun)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ++_depth;
                    try { action(); return Task.CompletedTask; }
                    finally { --_depth; }
                }
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _queue.Enqueue((action, completion, cancellationToken));
                return completion.Task;
            }
        }
        public void RunNext()
        {
            lock (_gate)
            {
                var item = _queue.Dequeue();
                ++_depth;
                try
                {
                    item.Token.ThrowIfCancellationRequested(); item.Action(); item.Completion.SetResult();
                }
                catch (Exception error) { item.Completion.SetException(error); }
                finally { --_depth; }
            }
        }
    }
}
