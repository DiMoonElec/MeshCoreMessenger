using Microsoft.Extensions.Logging.Abstractions;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreSharp.Exceptions;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class BleProfileEditorTests
{
    [Fact]
    public async Task ScanSelectSaveAndReloadBleProfileNeverConnectImplicitly()
    {
        await using var context = await Context.CreateAsync();
        var model = context.Model;
        model.ProfileName = "BLE profile";
        model.SelectedTransport = ConnectionTransportKind.Ble;
        Assert.Contains(ConnectionTransportKind.Ble, model.TransportKinds);
        Assert.False(model.IsValid);
        await context.Scan.RefreshAsync(Token);
        Assert.False(model.IsValid);
        Assert.Null(context.Scan.SelectedDevice);
        context.Scan.SelectedDevice = Assert.Single(context.Scan.Devices);
        Assert.True(model.IsDirty); Assert.True(model.CanSave);
        var draft = model.CreateDraft();
        Assert.Equal("device", draft.BleDeviceId);
        Assert.Null(draft.TcpHost); Assert.Null(draft.TcpPort);
        Assert.Null(draft.SerialPortName); Assert.Null(draft.BaudRate);
        await model.SaveCommand.ExecuteAsync(null);
        Assert.False(model.IsDirty);
        Assert.Equal("device", model.SavedProfile!.BleDeviceId);
        Assert.Null(await context.Manager.GetSelectedProfileAsync(Token));
        Assert.Equal(0, context.Supervisor.ConnectCalls);
        Assert.Empty(context.Supervisor.SwitchedProfiles);
        await model.LoadAsync(Token);
        model.SelectedProfile = Assert.Single(model.AvailableProfiles);
        Assert.Equal("device", model.BleDeviceId);
        Assert.Equal("Hint", context.Scan.SelectedDevice!.Name);
        Assert.True(model.Control.CanConnect);
        await model.Control.ConnectCommand.ExecuteAsync(null);
        Assert.Empty(context.Supervisor.SwitchedProfiles);
        Assert.Equal(model.SavedProfile.Id, (await context.Manager.GetSelectedProfileAsync(Token))!.Id);
        Assert.Equal(1, context.Supervisor.ConnectCalls);
    }

    [Fact]
    public async Task SavedOfflineBleIsVisibleOnUnsupportedPlatformAndOtherTransportDraftsClearBle()
    {
        await using var context = await Context.CreateAsync(supported: false);
        var saved = await context.Manager.SaveAndSelectAsync(new()
        {
            Name = "Offline BLE", Transport = ConnectionTransportKind.Ble, BleDeviceId = "saved", BleDeviceName = "Offline",
        }, Token);
        await context.Model.LoadAsync(Token);
        Assert.Equal(saved.Id, context.Model.SavedProfile!.Id);
        Assert.Equal("saved", context.Scan.SelectedDevice!.DeviceId);
        Assert.False(context.Scan.SelectedDevice.IsFound);
        Assert.False(context.Model.Control.CanConnect);
        Assert.Contains("Windows", context.Model.BlePlatformNotice);
        context.Model.SelectedTransport = ConnectionTransportKind.Tcp;
        context.Model.TcpHost = "localhost"; context.Model.TcpPort = "5000";
        var tcp = context.Model.CreateDraft();
        Assert.Null(tcp.BleDeviceId); Assert.Null(tcp.BleDeviceName);
        context.Model.SelectedTransport = ConnectionTransportKind.Serial;
        context.Model.SerialPortName = "COM1";
        var serial = context.Model.CreateDraft();
        Assert.Null(serial.BleDeviceId); Assert.Null(serial.BleDeviceName);
        Assert.Equal(0, context.Supervisor.ConnectCalls);
    }

    [Fact]
    public async Task CloseAndShutdownCancelScanAndPreventLateSelectionChanges()
    {
        await using var context = await Context.CreateAsync();
        context.Model.ProfileName = "BLE"; context.Model.SelectedTransport = ConnectionTransportKind.Ble;
        context.Catalog.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var scan = context.Scan.RefreshAsync(Token);
        Assert.False(context.Model.CanSave);
        Assert.False(context.Model.Control.CanConnect);
        await context.Model.CloseBleEditorAsync();
        await scan.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(context.Catalog.LastToken.IsCancellationRequested);
        context.Catalog.Pending.SetResult([new("late", "Late", true, true, true)]);
        Assert.Null(context.Model.BleDeviceId);
        context.Catalog.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = context.Scan.RefreshAsync(Token);
        await context.Model.StopAsync();
        await next.WaitAsync(TimeSpan.FromSeconds(5), Token);
        context.Catalog.Pending.SetResult([new("later", "Later", true, true, true)]);
        Assert.Null(context.Model.BleDeviceId);
        Assert.False(context.Model.CanSave);
    }

    [Theory]
    [InlineData(MeshCoreBleFailureReason.PairingRequired, "Сопрягите")]
    [InlineData(MeshCoreBleFailureReason.AccessDenied, "разрешения")]
    [InlineData(MeshCoreBleFailureReason.InsufficientMtu, "размер")]
    [InlineData(MeshCoreBleFailureReason.IncompleteNotification, "Целостность")]
    public void BleFailurePresentationHidesNativeDetails(MeshCoreBleFailureReason reason, string expected)
    {
        var description = ConnectionFailureClassifier.Describe(new MeshCoreBleTransportException(reason, "HRESULT=0xDEADBEEF"));
        Assert.Contains(expected, description);
        Assert.DoesNotContain("HRESULT", description);
        Assert.Equal("existing TCP error", ConnectionFailureClassifier.Describe(new IOException("existing TCP error")));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class Context : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MeshCoreMessenger.BleEditor.Tests", Guid.NewGuid().ToString("N"));
        private LocalStorage _storage = null!;
        public ConnectionProfileManager Manager { get; private set; } = null!;
        public FakeConnectionSupervisor Supervisor { get; } = new();
        public Catalog Catalog { get; } = new();
        public BleDeviceScanViewModel Scan { get; private set; } = null!;
        public ConnectionProfilesViewModel Model { get; private set; } = null!;
        public static async Task<Context> CreateAsync(bool supported = true)
        {
            var context = new Context();
            context._storage = await LocalStorage.OpenAsync(DesktopAppPaths.CreateForDirectory(context._directory), Token);
            context.Manager = new(context._storage.ConnectionProfiles, context._storage.Settings, TimeProvider.System);
            context.Catalog.IsSupported = supported;
            var dispatcher = new ImmediateUiDispatcher();
            context.Scan = new(context.Catalog, dispatcher, new Settings(), NullLogger<BleDeviceScanViewModel>.Instance);
            context.Model = new(context.Manager, context.Supervisor, new Ports(), NullLogger<ConnectionProfilesViewModel>.Instance,
                dispatcher, bleDevices: context.Scan);
            await context.Model.LoadAsync(Token);
            return context;
        }
        public async ValueTask DisposeAsync()
        {
            await Model.StopAsync(); await _storage.DisposeAsync(); Directory.Delete(_directory, true);
        }
    }
    private sealed class Catalog : IBleDeviceCatalog
    {
        public bool IsSupported { get; set; }
        public TaskCompletionSource<IReadOnlyList<BleDeviceChoice>>? Pending { get; set; }
        public CancellationToken LastToken { get; private set; }
        public Task<IReadOnlyList<BleDeviceChoice>> FindAsync(CancellationToken cancellationToken = default)
        {
            LastToken = cancellationToken;
            return Pending?.Task ?? Task.FromResult<IReadOnlyList<BleDeviceChoice>>([new("device", "Hint", true, true, true)]);
        }
    }
    private sealed class Settings : IBluetoothSettingsLauncher { public void Open() { } }
    private sealed class Ports : ISerialPortCatalog
    {
        public Task<IReadOnlyList<string>> GetPortNamesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
