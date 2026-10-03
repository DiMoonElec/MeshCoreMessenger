using CommunityToolkit.Mvvm.ComponentModel;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Desktop.ViewModels;

public sealed record HistoryClearTarget(Guid NodeId, Guid ConversationId, string NodeTitle, string ConversationTitle);

/// <summary>Captures the confirmation target; changing selection never retargets an accepted deletion.</summary>
public sealed class HistoryClearViewModel(Func<HistoryClearTarget?> capture, IHistoryClearService? service,
    Func<HistoryClearResult, Task> committed, Func<Task, Task>? track = null, CancellationToken cancellationToken = default) : ObservableObject
{
    private bool _canClear;
    private bool _busy;
    private string? _availabilityMessage;
    private string? _errorMessage;
    private long _revision;
    private HistoryClearTarget? _prepared;
    public bool CanClear { get => _canClear; private set => SetProperty(ref _canClear, value); }
    public bool IsBusy { get => _busy; private set => SetProperty(ref _busy, value); }
    public string? AvailabilityMessage { get => _availabilityMessage; private set => SetProperty(ref _availabilityMessage, value); }
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError)); }
    }
    public bool HasError => ErrorMessage is not null;

    public async Task RefreshAsync()
    {
        var revision = ++_revision;
        CanClear = false;
        _prepared = null;
        if (IsBusy) { AvailabilityMessage = "Очистка выполняется…"; return; }
        var target = capture();
        if (service is null || target is null) { AvailabilityMessage = "В этой переписке нет сохранённой истории."; return; }
        AvailabilityMessage = "Проверка…";
        try
        {
            var reason = await service.GetUnavailableReasonAsync(target.NodeId, target.ConversationId, cancellationToken);
            if (revision != _revision || target != capture()) return;
            AvailabilityMessage = reason;
            CanClear = reason is null;
            _prepared = CanClear ? target : null;
        }
        catch (Exception) { if (revision == _revision) AvailabilityMessage = "Не удалось проверить локальную историю."; }
    }

    public HistoryClearTarget? Capture() => CanClear && _prepared == capture() ? _prepared : null;

    public Task ClearAsync(HistoryClearTarget target)
    {
        var task = ClearCoreAsync(target);
        return track is null ? task : track(task);
    }

    private async Task ClearCoreAsync(HistoryClearTarget target)
    {
        if (service is null || IsBusy) return;
        ++_revision;
        IsBusy = true;
        CanClear = false;
        AvailabilityMessage = "Очистка выполняется…";
        ErrorMessage = null;
        try
        {
            var result = await service.ClearAsync(target.NodeId, target.ConversationId, cancellationToken);
            try { await committed(result); }
            catch (Exception) { ErrorMessage = "История удалена. Не удалось обновить список переписок; откройте переписку повторно."; }
        }
        catch (Exception exception)
        {
            ErrorMessage = exception is InvalidOperationException ? exception.Message : "Не удалось удалить историю. Сообщения сохранены.";
        }
        finally { IsBusy = false; await RefreshAsync(); }
    }
}
