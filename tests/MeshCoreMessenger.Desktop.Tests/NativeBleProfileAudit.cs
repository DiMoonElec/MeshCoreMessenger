using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreMessenger.Desktop.Views.Connection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshCoreMessenger.Desktop.Tests;

public static class NativeBleProfileAudit
{
    public static async Task RunAsync()
    {
        var output = Path.GetFullPath("artifacts/ble15");
        Directory.CreateDirectory(output);
        var data = Path.Combine(Path.GetTempPath(), "MeshCoreMessenger.BleNative", Guid.NewGuid().ToString("N"));
        try
        {
            await using var storage = await LocalStorage.OpenAsync(DesktopAppPaths.CreateForDirectory(data));
            var manager = new ConnectionProfileManager(storage.ConnectionProfiles, storage.Settings, TimeProvider.System);
            var saved = await manager.SaveAndSelectAsync(new()
            {
                Name = "BLE audit", Transport = ConnectionTransportKind.Ble, BleDeviceId = "saved-offline", BleDeviceName = "Сохранённая нода",
            });
            var supervisor = new FakeConnectionSupervisor();
            var scan = new BleDeviceScanViewModel(new Catalog(), new AvaloniaUiDispatcher(), new Settings(), NullLogger<BleDeviceScanViewModel>.Instance);
            var model = new ConnectionProfilesViewModel(manager, supervisor, new Ports(), NullLogger<ConnectionProfilesViewModel>.Instance,
                new AvaloniaUiDispatcher(), bleDevices: scan);
            await model.LoadAsync();
            var view = new ConnectionProfileEditorView { DataContext = model, Margin = new Thickness(16) };
            var window = new Window
            {
                Title = "BLE editor audit — fake devices", Width = 420, Height = 800,
                Content = new ScrollViewer { Content = view, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            };
            try
            {
                window.Show(); window.Activate();
                await SettleAsync();
                Require(model.BleDeviceId == saved.BleDeviceId && !model.IsDirty, "Attaching the view changed the saved BLE endpoint.");
                await scan.RefreshAsync();
                await SettleAsync();
                Require(model.BleDeviceId == saved.BleDeviceId, "Scan/ComboBox reset changed the saved selection.");
                var picker = view.GetVisualDescendants().OfType<ComboBox>().Single(combo => ReferenceEquals(combo.ItemsSource, scan.Devices));
                Require(picker.SelectedItem is BleDeviceChoice selected && selected.DeviceId == scan.SelectedDevice?.DeviceId,
                    $"BLE selected-item binding did not restore saved endpoint: picker={picker.SelectedItem}, model={scan.SelectedDevice}, index={picker.SelectedIndex}.");
                picker.SelectedItem = scan.Devices.First(device => device.DeviceId == "live-1");
                await SettleAsync();
                Require(model.BleDeviceId == "live-1" && model.IsDirty, "BLE device selection did not update the dirty draft.");
                await model.SaveCommand.ExecuteAsync(null);
                await SettleAsync();
                Require(!model.IsDirty && (await storage.ConnectionProfiles.GetAsync(saved.Id))?.BleDeviceId == "live-1", "BLE save failed through native bindings.");
                var transportPicker = view.GetVisualDescendants().OfType<ComboBox>()
                    .Single(combo => combo.ItemsSource is IEnumerable<ConnectionTransportKind>);
                foreach (var kind in new[] { ConnectionTransportKind.Tcp, ConnectionTransportKind.Serial,
                    ConnectionTransportKind.Ble, ConnectionTransportKind.Tcp, ConnectionTransportKind.Ble })
                {
                    transportPicker.SelectedItem = kind;
                    await SettleAsync();
                    Require(model.SelectedTransport == kind && transportPicker.SelectedItem is ConnectionTransportKind actual && actual == kind,
                        $"Transport picker lost {kind}: model={model.SelectedTransport}, selected={transportPicker.SelectedItem}.");
                    Require(model.IsTcp == (kind == ConnectionTransportKind.Tcp) &&
                        model.IsSerial == (kind == ConnectionTransportKind.Serial) && model.IsBle == (kind == ConnectionTransportKind.Ble),
                        $"Transport field visibility does not match {kind}.");
                    if (kind == ConnectionTransportKind.Tcp) { model.TcpHost = "localhost"; model.TcpPort = "6001"; }
                    else if (kind == ConnectionTransportKind.Serial) { model.SerialPortName = "COM_AUDIT"; model.BaudRate = "115200"; }
                    else scan.SelectedDevice = scan.Devices.First(device => device.DeviceId == "live-1");
                    Require(model.CanSave, $"Transport {kind} cannot be saved after editing valid settings.");
                    await model.SaveCommand.ExecuteAsync(null);
                    await SettleAsync();
                    var persisted = await storage.ConnectionProfiles.GetAsync(saved.Id);
                    Require(persisted?.Transport == kind && !model.IsDirty && transportPicker.SelectedItem is ConnectionTransportKind restored && restored == kind,
                        $"Saved transport {kind} was not retained by the UI and store.");
                    Console.WriteLine($"Native transport picker: {kind} selected, fields matched, saved and restored.");
                }
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                foreach (var width in new[] { 420, 900 })
                {
                    window.RequestedThemeVariant = theme; window.Width = width;
                    await SettleAsync();
                    Require(picker.Bounds.Width > 0 && picker.Bounds.Width <= window.ClientSize.Width, "BLE device picker overflows the window.");
                    var search = view.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Найти устройства"));
                    Require(search.Focus(NavigationMethod.Tab), "BLE scan button cannot receive keyboard focus.");
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(output, $"ble-editor-{theme}-{width}.png"), PngBitmapEncoderOptions.Default);
                }
                Require(supervisor.ConnectCalls == 0 && supervisor.SwitchedProfiles.Count == 0, "Editor performed an implicit connection.");
                Console.WriteLine($"BLE editor native binding/layout audit passed; fake discovery only, zero connects. Screenshots: {output}");
            }
            finally { await model.StopAsync(); window.Close(); }
        }
        finally { if (Directory.Exists(data)) Directory.Delete(data, true); }
    }

    private static void Require(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
    private static async Task SettleAsync()
    {
        await Task.Delay(80);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
    }
    private sealed class Catalog : IBleDeviceCatalog
    {
        public bool IsSupported => true;
        public Task<IReadOnlyList<BleDeviceChoice>> FindAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BleDeviceChoice>>([
                new("live-1", "Одинаковое имя 🐈", true, true, true),
                new("live-2", "Одинаковое имя 🐈", false, true, true),
            ]);
    }
    private sealed class Settings : IBluetoothSettingsLauncher { public void Open() => throw new InvalidOperationException("Settings launch is not part of this audit."); }
    private sealed class Ports : ISerialPortCatalog
    {
        public Task<IReadOnlyList<string>> GetPortNamesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
