using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Desktop.ViewModels;

public sealed record RegionDiscoveryListItem(string Name, string Sources, string Note, DiscoveredRegionNameKind Kind);
public sealed record RegionDiscoveryOutcomeItem(string Target, string Status, string Restoration);

public sealed partial class RegionSettingsViewModel
{
    private CancellationTokenSource? _searchStop;
    private long _searchVersion;
    private bool _searching, _allowCreation, _allowZeroHop;
    private string _discoveryStatus = "Поиск отправляет запросы в эфир. Ответы могут быть неполными.";
    private RegionDiscoveryListItem? _foundSelection;
    public ObservableCollection<RegionDiscoveryListItem> FoundRegions { get; } = [];
    public ObservableCollection<RegionDiscoveryOutcomeItem> DiscoveryOutcomes { get; } = [];
    public AsyncRelayCommand SearchCommand { get; private set; } = null!;
    public RelayCommand CancelSearchCommand { get; private set; } = null!;
    public AsyncRelayCommand AddFoundCommand { get; private set; } = null!;
    public bool CanSearch => CanEdit && IsOnline && _commands is not null && _commands.GetRegionCapabilities(_node!.Value)?.NeighborDiscovery != RegionSupport.Unsupported;
    public bool IsSearching { get => _searching; private set { if (SetProperty(ref _searching, value)) { CancelSearchCommand?.NotifyCanExecuteChanged(); NotifyCommands(); } } }
    public bool AllowContactCreation { get => _allowCreation; set => SetProperty(ref _allowCreation, value); }
    public bool AllowTemporaryZeroHop { get => _allowZeroHop; set => SetProperty(ref _allowZeroHop, value); }
    public string DiscoveryStatus { get => _discoveryStatus; private set => SetProperty(ref _discoveryStatus, value); }
    public RegionDiscoveryListItem? SelectedFound
    {
        get => _foundSelection;
        set { if (SetProperty(ref _foundSelection, value)) AddFoundCommand?.NotifyCanExecuteChanged(); }
    }
    private void ConfigureDiscovery()
    {
        SearchCommand = new AsyncRelayCommand(() => _track(SearchAsync()), () => CanSearch);
        CancelSearchCommand = new RelayCommand(() => { if (_searchStop is { } stop) _ = _track(stop.CancelAsync()); }, () => IsSearching);
        AddFoundCommand = Command(AddFoundAsync, () => SelectedFound?.Kind is DiscoveredRegionNameKind.Public or DiscoveredRegionNameKind.PrivateKeyRequired);
    }
    private async Task SearchAsync()
    {
        if (!CanSearch || _node is not { } node) return;
        var context = _context; var operation = ++_searchVersion;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_contextStop.Token);
        _searchStop = stop; IsBusy = true; IsSearching = true; Error = null;
        FoundRegions.Clear(); DiscoveryOutcomes.Clear(); SelectedFound = null;
        DiscoveryStatus = "Поиск соседних репитеров…";
        var options = new RegionDiscoveryOptions { Consent = new(AllowContactCreation, AllowTemporaryZeroHop) };
        try
        {
            var owner = CaptureOwner(node);
            var progress = new DiscoveryProgress(p =>
            {
                if (_stopped) return;
                _ = _track(_dispatcher.InvokeAsync(() =>
                {
                    if (Current(context) && operation == _searchVersion && IsSearching)
                        DiscoveryStatus = p.Phase switch
                        {
                            RegionDiscoveryPhase.Scanning => "Поиск соседних репитеров…",
                            RegionDiscoveryPhase.Preparing => "Проверка маршрутов репитеров…",
                            RegionDiscoveryPhase.Requesting => $"Запрос регионов: {p.Completed} из {p.Total}…",
                            _ => "Завершение поиска…",
                        };
                }));
            });
            var result = await _service!.DiscoverAsync(owner, options, progress, stop.Token);
            await _dispatcher.InvokeAsync(() =>
            {
                if (!Current(context) || operation != _searchVersion) return;
                foreach (var name in result.Names)
                    FoundRegions.Add(new(name.ExactName, string.Join(Environment.NewLine, name.SourcePublicKeys), name.Kind switch
                    {
                        DiscoveredRegionNameKind.PrivateKeyRequired => "Нужен исходный приватный ключ",
                        DiscoveredRegionNameKind.Unscoped => "Без региона; добавлять в каталог не требуется",
                        DiscoveredRegionNameKind.Unrecognized => "Нераспознанное имя; сохранено только как наблюдение",
                        _ => $"Источников: {name.SourcePublicKeys.Count}",
                    }, name.Kind));
                foreach (var outcome in result.Outcomes)
                    DiscoveryOutcomes.Add(new(outcome.RepeaterPublicKeyHex, OutcomeLabel(outcome.Status), outcome.Restoration switch
                    {
                        RegionRouteRestorationOutcome.Restored => "Маршрут восстановлен",
                        RegionRouteRestorationOutcome.NotRequired => "Изменение маршрута не требовалось",
                        RegionRouteRestorationOutcome.Conflict => "Маршрут изменился; новое состояние сохранено",
                        _ => "Восстановление маршрута не подтверждено: " + outcome.Restoration,
                    }));
                DiscoveryStatus = $"Найдено имён: {result.Names.Count}, результатов репитеров: {result.Outcomes.Count}. Список может быть неполным." +
                    (result.Cancelled ? " Поиск отменён." : "") + (result.DeadlineReached ? " Истекло общее время поиска." : "") +
                    (result.SessionEnded ? " Сессия завершена." : "") + (result.Error is not null ? " Поиск завершён с ошибкой." : "") +
                    (result.OmittedTargets > 0 ? $" Не опрошено из-за лимита: {result.OmittedTargets}." : "");
            });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { await _dispatcher.InvokeAsync(() => { if (Current(context)) DiscoveryStatus = "Поиск отменён."; }); }
        catch (Exception)
        { await ShowErrorAsync(context, "Не удалось выполнить поиск. Проверьте подключение; повторный поиск запускается только по кнопке."); }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_searchStop, stop)) _searchStop = null;
                if (Current(context) && operation == _searchVersion)
                { AllowContactCreation = false; AllowTemporaryZeroHop = false; IsSearching = false; IsBusy = false; }
            });
        }
    }
    private async Task AddFoundAsync(Guid node, long context, CancellationToken token)
    {
        var selected = SelectedFound;
        if (selected is null) return;
        if (selected.Kind == DiscoveredRegionNameKind.PrivateKeyRequired)
        {
            Selected = null; Label = selected.Name; ProtocolName = selected.Name; KeyHex = "";
            await ShowErrorAsync(context, "Имя приватного региона перенесено в форму. Введите исходный ключ и нажмите «Импортировать».");
        }
        else if (selected.Kind == DiscoveredRegionNameKind.Public)
            await _service!.CreatePublicAsync(node, selected.Name, selected.Name, token);
    }
    private void ResetDiscovery()
    {
        _searchVersion++; _searchStop = null; IsSearching = false;
        FoundRegions.Clear(); DiscoveryOutcomes.Clear(); SelectedFound = null;
        AllowContactCreation = false; AllowTemporaryZeroHop = false;
        DiscoveryStatus = "Поиск отправляет запросы в эфир. Ответы могут быть неполными.";
    }
    private static string OutcomeLabel(RegionDiscoveryTargetStatus status) => status switch
    {
        RegionDiscoveryTargetStatus.SuccessEmpty => "Ответ получен: список пуст",
        RegionDiscoveryTargetStatus.SuccessPossiblyPartial => "Ответ получен; возможно неполный",
        RegionDiscoveryTargetStatus.SuccessWithRouteWarning => "Ответ получен; проверьте восстановление маршрута",
        RegionDiscoveryTargetStatus.PreparationRequired => "Требуется разрешение на подготовку контакта/маршрута",
        RegionDiscoveryTargetStatus.NoDirectPath => "Нет подходящего прямого маршрута",
        RegionDiscoveryTargetStatus.Timeout => "Нет ответа за отведённое время",
        RegionDiscoveryTargetStatus.ContactTableFull => "Таблица контактов ноды заполнена",
        RegionDiscoveryTargetStatus.Unsupported => "Не поддерживается",
        RegionDiscoveryTargetStatus.Cancelled => "Отменено",
        RegionDiscoveryTargetStatus.Malformed => "Некорректный ответ",
        RegionDiscoveryTargetStatus.StorageFailed => "Ошибка сохранения; следующие запросы остановлены",
        RegionDiscoveryTargetStatus.StalePlan => "Контакт изменился; повторите поиск",
        _ => "Ошибка запроса: " + status,
    };
    private sealed class DiscoveryProgress(Action<RegionDiscoveryProgress> apply) : IProgress<RegionDiscoveryProgress>
    { public void Report(RegionDiscoveryProgress value) => apply(value); }
}
