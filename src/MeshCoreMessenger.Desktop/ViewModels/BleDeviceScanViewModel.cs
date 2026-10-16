using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreSharp.Exceptions;

namespace MeshCoreMessenger.Desktop.ViewModels;

public sealed record BleDeviceChoice(string DeviceId, string? Name, bool IsPaired,
    bool AdvertisesCompanionService, bool IsFound)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Без имени" : Name;
    public string EndpointHint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DeviceId)))[..8];
    public string Status => !IsFound ? "Не найден при последнем поиске" : IsPaired ? "Сопряжено с Windows" : "Требуется сопряжение с Windows";
}

public enum BleScanState { Idle, Scanning, Completed, Error }

/// <summary>Discovery presentation only. Never saves profiles, pairs or opens Companion sessions.</summary>
public sealed class BleDeviceScanViewModel : ObservableObject
{
    private readonly IBleDeviceCatalog _catalog;
    private readonly IUiDispatcher _dispatcher;
    private readonly IBluetoothSettingsLauncher _settings;
    private readonly ILogger<BleDeviceScanViewModel> _logger;
    private CancellationTokenSource? _current;
    private long _generation;
    private bool _stopped;
    private BleScanState _state;
    private string? _error;
    private BleDeviceChoice? _selected;

    public BleDeviceScanViewModel(IBleDeviceCatalog catalog, IUiDispatcher dispatcher,
        IBluetoothSettingsLauncher settings, ILogger<BleDeviceScanViewModel> logger)
    {
        _catalog = catalog; _dispatcher = dispatcher; _settings = settings; _logger = logger;
        ScanCommand = new AsyncRelayCommand(RefreshAsync, () => IsSupported && !_stopped,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        CancelCommand = new AsyncRelayCommand(CancelAsync, () => IsScanning);
        OpenBluetoothSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync, () => IsSupported && !_stopped);
    }

    public ObservableCollection<BleDeviceChoice> Devices { get; } = [];
    public bool IsSupported => _catalog.IsSupported;
    public BleScanState State { get => _state; private set { if (SetProperty(ref _state, value)) NotifyState(); } }
    public bool IsScanning => State == BleScanState.Scanning;
    public string? ErrorMessage { get => _error; private set => SetProperty(ref _error, value); }
    public string PairingInstructions => "Сопрягите ноду в настройках Bluetooth Windows, затем явно обновите список устройств. PIN в приложении не сохраняется.";
    public BleDeviceChoice? SelectedDevice { get => _selected; set => SetProperty(ref _selected, value); }
    public IAsyncRelayCommand ScanCommand { get; }
    public IAsyncRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand OpenBluetoothSettingsCommand { get; }

    public Task LoadEndpointAsync(string? deviceId, string? name) => _dispatcher.InvokeAsync(() =>
    {
        if (_stopped) return;
        CancelCurrent();
        SelectedDevice = string.IsNullOrWhiteSpace(deviceId) ? null :
            Devices.FirstOrDefault(item => item.DeviceId == deviceId) ?? new(deviceId, name, false, false, false);
        ReplaceDevices(Devices.Where(item => item.IsFound).ToArray());
        State = BleScanState.Idle; ErrorMessage = null;
    });

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? scope = null;
        long generation = 0;
        await _dispatcher.InvokeAsync(() =>
        {
            if (_stopped || !IsSupported) return;
            CancelCurrent();
            _current = scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            generation = _generation;
            ErrorMessage = null; State = BleScanState.Scanning;
        }, cancellationToken);
        if (scope is null) return;
        bool IsCurrent() => !_stopped && generation == _generation && ReferenceEquals(scope, _current);
        try
        {
            var finding = _catalog.FindAsync(scope.Token);
            _ = ObserveAsync(finding);
            var found = await finding.WaitAsync(scope.Token).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrent() || scope.IsCancellationRequested) return;
                ReplaceDevices(found);
                State = BleScanState.Completed;
            });
        }
        catch (OperationCanceledException) when (scope.IsCancellationRequested)
        {
            await _dispatcher.InvokeAsync(() => { if (IsCurrent()) State = BleScanState.Idle; });
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "BLE device scan failed");
            await _dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrent() || scope.IsCancellationRequested) return;
                ErrorMessage = error is MeshCoreBleTransportException ble ? ble.Reason switch
                {
                    MeshCoreBleFailureReason.RadioOff => "Включите Bluetooth и повторите поиск.",
                    MeshCoreBleFailureReason.AdapterUnavailable => "Адаптер Bluetooth недоступен. Проверьте его и повторите поиск.",
                    MeshCoreBleFailureReason.AccessDenied => "Windows запретила доступ к Bluetooth. Проверьте разрешения.",
                    MeshCoreBleFailureReason.UnsupportedPlatform => "Поиск BLE доступен только на Windows.",
                    _ => "Не удалось найти BLE-устройства. Повторите поиск.",
                } : "Не удалось найти BLE-устройства. Повторите поиск.";
                State = BleScanState.Error;
            });
        }
        finally
        {
            await _dispatcher.InvokeAsync(() => { if (ReferenceEquals(scope, _current)) _current = null; });
            scope.Dispose();
        }
    }

    // Editor close cancels the active generation; it may be reopened and scanned explicitly.
    public Task CancelAsync() => _dispatcher.InvokeAsync(() =>
    {
        CancelCurrent(); State = BleScanState.Idle;
    });

    public Task StopAsync() => _dispatcher.InvokeAsync(() =>
    {
        if (_stopped) return;
        _stopped = true;
        CancelCurrent(); State = BleScanState.Idle; NotifyState();
    });

    private Task OpenSettingsAsync() => _dispatcher.InvokeAsync(() =>
    {
        if (_stopped || !IsSupported) return;
        CancelCurrent(); State = BleScanState.Idle; ErrorMessage = null;
        try { _settings.Open(); }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Could not open Bluetooth settings");
            ErrorMessage = "Не удалось открыть настройки. Откройте Bluetooth в параметрах Windows.";
            State = BleScanState.Error;
        }
    });

    private void CancelCurrent()
    {
        ++_generation;
        _current?.Cancel();
        _current = null;
    }

    private void ReplaceDevices(IReadOnlyList<BleDeviceChoice> found)
    {
        var selected = SelectedDevice;
        var choices = found.DistinctBy(item => item.DeviceId, StringComparer.Ordinal).ToList();
        if (selected is not null && choices.All(item => item.DeviceId != selected.DeviceId))
            choices.Add(selected with { IsFound = false, IsPaired = false, AdvertisesCompanionService = false });
        Devices.Clear();
        foreach (var device in choices.OrderBy(item => item.DisplayName, StringComparer.CurrentCulture)
            .ThenBy(item => item.DeviceId, StringComparer.Ordinal)) Devices.Add(device);
        SelectedDevice = selected is null ? null : Devices.First(item => item.DeviceId == selected.DeviceId);
        // ComboBox drops its visual selection when Items is rebuilt even when the
        // saved DTO compares equal. Reapply the binding after the collection reset.
        OnPropertyChanged(nameof(SelectedDevice));
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsScanning));
        ScanCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        OpenBluetoothSettingsCommand.NotifyCanExecuteChanged();
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { /* The awaited scan reports current failures; abandoned results have no UI owner. */ }
    }
}
