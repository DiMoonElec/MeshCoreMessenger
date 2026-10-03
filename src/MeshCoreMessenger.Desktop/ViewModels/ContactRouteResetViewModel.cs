using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshCoreMessenger.Core.Application;
using MeshCoreSharp.Exceptions;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Revision-safe menu availability. Captures an exact session/contact on explicit invocation.</summary>
public sealed class ContactRouteResetViewModel : ObservableObject
{
    private Func<ContactRouteResetRequest?> _capture = () => null;
    private IContactRouteService? _service;
    private Func<ContactRouteResetResult, Task> _committed = _ => Task.CompletedTask;
    private Func<Task, Task> _track = task => task;
    private CancellationToken _token;
    private long _revision;
    private bool _canReset;
    private bool _busy;
    private string? _availabilityMessage = "Нужна активная сессия личного контакта.";
    private string? _statusMessage;
    private ContactRouteResetRequest? _prepared;
    public ContactRouteResetViewModel() => Command = new AsyncRelayCommand(ExecuteAsync, () => CanReset && !IsBusy);
    public AsyncRelayCommand Command { get; }
    public bool CanReset { get => _canReset; private set { if (SetProperty(ref _canReset, value)) Command.NotifyCanExecuteChanged(); } }
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) Command.NotifyCanExecuteChanged(); } }
    public string? AvailabilityMessage { get => _availabilityMessage; private set => SetProperty(ref _availabilityMessage, value); }
    public string? StatusMessage { get => _statusMessage; private set { if (SetProperty(ref _statusMessage, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => StatusMessage is not null;

    internal void Configure(Func<ContactRouteResetRequest?> capture, IContactRouteService? service,
        Func<ContactRouteResetResult, Task> committed, Func<Task, Task> track, CancellationToken token)
    {
        _capture = capture; _service = service; _committed = committed; _track = track; _token = token;
        Invalidate();
    }
    public void Invalidate()
    {
        ++_revision; _prepared = null; CanReset = false;
        AvailabilityMessage = IsBusy ? "Сброс маршрута выполняется…" : "Откройте меню для проверки доступности.";
        StatusMessage = null;
    }
    public Task RefreshAsync() => _track(RefreshCoreAsync());
    private async Task RefreshCoreAsync()
    {
        var revision = ++_revision; CanReset = false; _prepared = null;
        var request = _capture();
        if (_service is null || request is null) { AvailabilityMessage = "Нужна активная сессия личного контакта."; return; }
        if (IsBusy) { AvailabilityMessage = "Сброс маршрута выполняется…"; return; }
        AvailabilityMessage = "Проверка…";
        try
        {
            var reason = await _service.GetUnavailableReasonAsync(request, _token);
            if (revision != _revision || !Same(request, _capture())) return;
            AvailabilityMessage = reason; CanReset = reason is null;
            _prepared = CanReset ? request with { PublicKey = request.PublicKey.ToArray() } : null;
        }
        catch (Exception) { if (revision == _revision) AvailabilityMessage = "Не удалось проверить маршрут."; }
    }
    private Task ExecuteAsync() => _track(ResetAsync());
    private async Task ResetAsync()
    {
        if (_service is null || !CanReset || IsBusy || _prepared is not { } request || !Same(request, _capture())) return;
        ++_revision; IsBusy = true; CanReset = false; StatusMessage = null;
        string? status;
        try
        {
            var result = await _service.ResetAsync(request, _token);
            status = result.Warning ?? "Маршрут сброшен.";
            if (result.LocalUpdated)
            {
                try { await _committed(result); }
                catch (Exception) { status = "Маршрут сброшен. Не удалось обновить отображение; откройте переписку повторно."; }
            }
        }
        catch (MeshCoreCommandException) { status = "Нода отклонила сброс маршрута."; }
        catch (InvalidOperationException exception) { status = exception.Message; }
        catch (Exception) { status = "Не удалось подтвердить сброс маршрута. Проверьте подключение и состояние контакта."; }
        finally { IsBusy = false; }
        if (Same(request, _capture())) StatusMessage = status;
        await RefreshAsync();
    }
    private static bool Same(ContactRouteResetRequest? a, ContactRouteResetRequest? b) => a is not null && b is not null &&
        a.NodeId == b.NodeId && a.SessionId == b.SessionId && a.Generation == b.Generation && a.PublicKey.Span.SequenceEqual(b.PublicKey.Span);
}
