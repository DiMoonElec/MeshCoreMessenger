using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Presentation;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Explicit fresh private sends from failed bubbles, independent of composer draft contents.</summary>
internal sealed class PrivateResendCoordinator : IDisposable
{
    private readonly ChatWorkspaceViewModel _workspace;
    private readonly IConnectionSupervisor _supervisor;
    private readonly IMessageService? _service;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<Task, Task> _track;
    private readonly CancellationToken _token;
    private readonly ILogger _logger;
    private readonly Dictionary<HistoryMessageListItem, AsyncRelayCommand> _commands = [];
    private bool _busy, _stopped;

    public PrivateResendCoordinator(ChatWorkspaceViewModel workspace, IConnectionSupervisor supervisor,
        IMessageService? service, IUiDispatcher dispatcher, Func<Task, Task> track, CancellationToken token, ILogger logger)
    {
        _workspace = workspace; _supervisor = supervisor; _service = service; _dispatcher = dispatcher;
        _track = track; _token = token; _logger = logger;
        if (service is null) return;
        workspace.Navigation.History.Messages.CollectionChanged += OnCollectionChanged;
        workspace.Composer.PropertyChanged += OnContextChanged;
        workspace.Composer.SendCommand.PropertyChanged += OnContextChanged;
        workspace.Navigation.PropertyChanged += OnContextChanged;
        workspace.PropertyChanged += OnContextChanged;
        Rebind();
    }
    private static bool Eligible(HistoryMessageListItem item) => item.IsOutgoing && item.Kind == StoredMessageKind.Text
        && item.Presentation.State is MessageSendDisplayState.Failed or MessageSendDisplayState.Unconfirmed;
    private bool CanSend(HistoryMessageListItem item)
    {
        var owner = _supervisor.Snapshot;
        return !_stopped && !_busy && _service is not null && Eligible(item)
            && !_workspace.Composer.SendCommand.IsRunning && _workspace.Composer.Readiness == SendReadiness.Ready
            && owner.State == ConnectionSupervisorState.Online && owner.SessionId is not null && owner.NodeId == _workspace.ViewedNode?.Id
            && _workspace.SelectedConversation is { Kind: ConversationKind.Contact } selected && selected.Id == item.ConversationId;
    }
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => Rebind();
    private void OnContextChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ComposerViewModel.Readiness) or nameof(AsyncRelayCommand.IsRunning)
            or nameof(ChatWorkspaceViewModel.ViewedNode) or nameof(ConversationNavigationViewModel.SelectedConversation)) Rebind();
    }
    private void OnMessageChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(HistoryMessageListItem.Presentation) && sender is HistoryMessageListItem item) Apply(item); }
    private void Rebind()
    {
        if (_stopped) return;
        var current = _workspace.Navigation.Messages;
        foreach (var item in _commands.Keys.Where(i => !current.Contains(i)).ToArray())
        { item.PropertyChanged -= OnMessageChanged; _commands.Remove(item); }
        foreach (var item in current)
        {
            if (!_commands.ContainsKey(item))
            { _commands[item] = new AsyncRelayCommand(() => _track(SendAsync(item)), () => CanSend(item)); item.PropertyChanged += OnMessageChanged; }
            Apply(item);
        }
    }
    private void Apply(HistoryMessageListItem item)
    {
        if (!_commands.TryGetValue(item, out var command)) return;
        item.SetSendAsNewAction(command, Eligible(item), CanSend(item), "Отправить еще раз");
        command.NotifyCanExecuteChanged();
    }
    private async Task SendAsync(HistoryMessageListItem item)
    {
        if (!CanSend(item)) return;
        var owner = _supervisor.Snapshot;
        var request = new PrivateResendRequest(owner.NodeId!.Value, owner.SessionId!.Value, owner.Generation,
            item.Id, _workspace.Composer.Options);
        _busy = true; Rebind();
        try { await _service!.SendPrivateAsNewAsync(request, _token); }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Could not resend private message {MessageId}", item.Id);
            await _dispatcher.InvokeAsync(() => _workspace.Composer.SetActionError("Не удалось отправить сообщение ещё раз. Проверьте его статус и подключение."));
        }
        finally { _busy = false; if (!_stopped) Rebind(); }
    }
    public void Dispose()
    {
        _stopped = true;
        _workspace.Navigation.History.Messages.CollectionChanged -= OnCollectionChanged;
        _workspace.Composer.PropertyChanged -= OnContextChanged;
        _workspace.Composer.SendCommand.PropertyChanged -= OnContextChanged;
        _workspace.Navigation.PropertyChanged -= OnContextChanged;
        _workspace.PropertyChanged -= OnContextChanged;
        foreach (var item in _commands.Keys) item.PropertyChanged -= OnMessageChanged;
        _commands.Clear();
    }
}
