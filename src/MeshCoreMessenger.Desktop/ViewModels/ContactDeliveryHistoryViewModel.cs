using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Read-only, paginated analytics for the card's captured node and full contact key.</summary>
public sealed class ContactDeliveryHistoryViewModel : ObservableObject, IDisposable
{
    private readonly Guid _nodeId;
    private readonly byte[] _key;
    private readonly IContactDeliveryHistoryReader? _reader;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private ContactDeliveryCursor? _cursor;
    private long _revision;
    private bool _disposed, _loading;
    private string? _error;
    private int _deliveryCount;

    public ContactDeliveryHistoryViewModel(Guid nodeId, ReadOnlyMemory<byte> key,
        IContactDeliveryHistoryReader? reader, IUiDispatcher dispatcher, ILogger logger, Func<Task, Task> track)
    {
        _nodeId = nodeId; _key = key.ToArray(); _reader = reader; _dispatcher = dispatcher; _logger = logger;
        RefreshCommand = new AsyncRelayCommand(() => track(RefreshAsync()), () => !IsLoading && !_disposed);
        LoadMoreCommand = new AsyncRelayCommand(() => track(ReadAsync(false)), () => HasMore && !IsLoading && !_disposed);
    }

    public ObservableCollection<ContactDeliveryRowViewModel> Rows { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand LoadMoreCommand { get; }
    public bool HasMore => _cursor is not null;
    public bool IsEmpty => !IsLoading && Rows.Count == 0 && Error is null;
    public string Summary => $"Доставок: {_deliveryCount} · подтверждений: {Rows.Count}";
    public string? Error { get => _error; private set { if (SetProperty(ref _error, value)) { OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(IsEmpty)); } } }
    public bool HasError => Error is not null;
    public bool IsLoading
    {
        get => _loading;
        private set
        {
            if (!SetProperty(ref _loading, value)) return;
            OnPropertyChanged(nameof(IsEmpty)); RefreshCommand.NotifyCanExecuteChanged(); LoadMoreCommand.NotifyCanExecuteChanged();
        }
    }

    public Task RefreshAsync() => ReadAsync(true);
    private async Task ReadAsync(bool refresh)
    {
        if (_disposed || (!refresh && (IsLoading || _cursor is null))) return;
        var revision = ++_revision;
        var token = _lifetime.Token;
        var cursor = refresh ? null : _cursor;
        IsLoading = true; Error = null;
        try
        {
            var page = _reader is null ? new ContactDeliveryPage([], null)
                : await _reader.GetPageAsync(_nodeId, _key, 25, cursor, token).ConfigureAwait(false);
            var rows = page.Items.SelectMany(delivery => delivery.Evidence.Select(evidence => new ContactDeliveryRowViewModel(delivery, evidence))).ToArray();
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || revision != _revision) return;
                if (refresh) { Rows.Clear(); _deliveryCount = 0; }
                foreach (var row in rows) Rows.Add(row);
                _deliveryCount += page.Items.Count;
                _cursor = page.NextCursor;
                OnPropertyChanged(nameof(HasMore)); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(IsEmpty));
                LoadMoreCommand.NotifyCanExecuteChanged();
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to read delivery history for node {NodeId}", _nodeId);
            await _dispatcher.InvokeAsync(() => { if (!_disposed && revision == _revision) Error = "Не удалось прочитать историю доставок. Попробуйте обновить."; }).ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() => { if (!_disposed && revision == _revision) IsLoading = false; }).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; ++_revision; _lifetime.Cancel(); _lifetime.Dispose();
        RefreshCommand.NotifyCanExecuteChanged(); LoadMoreCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One ACK evidence row; all candidates remain visible instead of guessing the successful attempt.</summary>
public sealed class ContactDeliveryRowViewModel
{
    public ContactDeliveryRowViewModel(ContactDeliveryRecord delivery, ContactDeliveryEvidence evidence)
    {
        AckTime = evidence.AckReceivedUtc.ToOffset(TimeSpan.FromMinutes(delivery.PcUtcOffsetMinutes)).ToString("dd.MM.yyyy\nHH:mm:ss.fff zzz");
        Attribution = evidence.Attribution switch
        {
            DeliveryAttribution.MultipleCandidates => "Несколько возможных попыток",
            DeliveryAttribution.Unknown => "Попытка неизвестна",
            _ => "Подтверждённая доставка",
        };
        if (delivery.WasLate) Attribution += " · поздний ACK";
        ConfiguredRoutes = evidence.Candidates.Count == 0 ? "Нет снимка"
            : string.Join("\n\n", evidence.Candidates.Select(candidate => $"Попытка {candidate.AttemptNumber}\n{FormatRoute(candidate.ConfiguredRoute)}"));
        LearnedRoute = FormatRoute(evidence.LearnedRoute);
        RoundTrip = evidence.RoundTripMilliseconds is { } milliseconds ? $"{milliseconds} мс" : "—";
        Diagnostics = $"ACK: {evidence.AckTag:X8}\nACK UTC: {evidence.AckReceivedUtc:O}\nЧасовой пояс при первом ACK: {delivery.PcTimeZoneId}\n{Attribution}";
        foreach (var candidate in evidence.Candidates)
        {
            var sent = candidate.SentUtc is { } utc
                ? (candidate.PcUtcOffsetMinutes is { } offset ? utc.ToOffset(TimeSpan.FromMinutes(offset)) : utc).ToString("dd.MM.yyyy HH:mm:ss.fff zzz") : "не сохранено";
            Diagnostics += $"\nПопытка {candidate.AttemptNumber}: отправка {sent}; timestamp={candidate.WireTimestamp}, attempt={candidate.WireAttempt}; {candidate.Phase}";
        }
        if (evidence.LearnedRoute is { } learned) Diagnostics += $"\nМаршрут после ACK прочитан: {learned.ObservedUtc:O}";
    }

    public string AckTime { get; }
    public string Attribution { get; }
    public string ConfiguredRoutes { get; }
    public string LearnedRoute { get; }
    public string RoundTrip { get; }
    public string Diagnostics { get; }

    private static string FormatRoute(PrivateRouteSnapshot? route)
    {
        if (route is null) return "Нет снимка";
        var summary = ContactRouteFormatter.FormatSummary(route.Descriptor, route.Path.ToArray());
        if (route.Kind != PrivateRouteKind.Path) return summary;
        var path = Enumerable.Range(0, route.HopCount)
            .Select(index => Convert.ToHexString(route.Path.Span.Slice(index * route.HashSize, route.HashSize)).ToLowerInvariant());
        return $"{summary}\n{string.Join(" → ", path)}";
    }
}
