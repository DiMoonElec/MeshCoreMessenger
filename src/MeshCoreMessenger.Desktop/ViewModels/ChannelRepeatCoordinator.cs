using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;
using Microsoft.Extensions.Logging;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Wires repeats only to outgoing channel bubbles; draft editing remains independent.</summary>
internal sealed class ChannelRepeatCoordinator : IDisposable
{
    private readonly ChatWorkspaceViewModel _workspace;
    private readonly IConnectionSupervisor _supervisor;
    private readonly IMessageService? _service;
    private readonly IOutgoingMessageStore? _store;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<Task, Task> _track;
    private readonly CancellationToken _token;
    private readonly ILogger _logger;
    private readonly Dictionary<HistoryMessageListItem, (AsyncRelayCommand Repeat, AsyncRelayCommand AsNew)> _commands = [];
    private bool _busy, _stopped;

    public ChannelRepeatCoordinator(ChatWorkspaceViewModel workspace, IConnectionSupervisor supervisor,
        IMessageService? service, IOutgoingMessageStore? store, IUiDispatcher dispatcher,
        Func<Task, Task> track, CancellationToken token, ILogger logger)
    {
        _workspace = workspace; _supervisor = supervisor; _service = service; _store = store;
        _dispatcher = dispatcher; _track = track; _token = token; _logger = logger;
        if (service is null || store is null) return;
        workspace.Navigation.History.Messages.CollectionChanged += OnCollectionChanged;
        workspace.Composer.PropertyChanged += OnContextChanged;
        workspace.PropertyChanged += OnContextChanged;
        workspace.Navigation.PropertyChanged += OnContextChanged;
        Rebind();
    }
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => Rebind();
    private void Rebind()
    {
        if (_stopped) return;
        var current = _workspace.Navigation.History.Messages;
        foreach (var item in _commands.Keys.Where(item => !current.Contains(item)).ToArray())
        {
            item.PropertyChanged -= OnMessageChanged; _commands.Remove(item);
        }
        foreach (var item in current)
        {
            if (!_commands.ContainsKey(item))
            {
                _commands[item] = (new AsyncRelayCommand(() => _track(RepeatAsync(item, false)), () => CanRepeat(item)),
                    new AsyncRelayCommand(() => _track(RepeatAsync(item, true)), () => CanRepeat(item)));
                item.PropertyChanged += OnMessageChanged;
            }
            Apply(item);
        }
    }
    private void OnMessageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(HistoryMessageListItem.Presentation) && sender is HistoryMessageListItem item) Apply(item);
    }
    private void OnContextChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_stopped || args.PropertyName is not (nameof(ComposerViewModel.Readiness) or nameof(ComposerViewModel.CanSend)
            or nameof(ComposerViewModel.SelectedSlot) or nameof(ChatWorkspaceViewModel.ViewedNode)
            or nameof(ConversationNavigationViewModel.SelectedConversation))) return;
        // Context publications already own the UI thread once the window is running.
        // Offline LoadAsync also publishes here before Avalonia is initialized; queuing
        // then would create its dispatcher on the storage continuation's thread.
        Rebind();
    }
    private bool Eligible(HistoryMessageListItem item) => item.IsOutgoing && item.Kind == StoredMessageKind.Text && item.Presentation.AttemptNumber is > 0;
    private bool CanRepeat(HistoryMessageListItem item)
    {
        var owner = _supervisor.Snapshot;
        return !_stopped && !_busy && _service is not null && _store is not null && Eligible(item) &&
            item.Presentation.State != Presentation.MessageSendDisplayState.Sending &&
            !_workspace.Composer.SendCommand.IsRunning &&
            owner.State == ConnectionSupervisorState.Online && owner.SessionId is not null && owner.NodeId == _workspace.ViewedNode?.Id &&
            _workspace.SelectedConversation is { Kind: ConversationKind.Channel } selected && selected.Id == item.ConversationId &&
            selected.Entry.ActiveChannelSlots.Count > 0;
    }
    private void Apply(HistoryMessageListItem item)
    {
        if (!_commands.TryGetValue(item, out var command)) return;
        item.SetRetryAction(command.Repeat, Eligible(item), CanRepeat(item), requiresConfirmation: false);
        item.SetSendAsNewAction(command.AsNew, Eligible(item), CanRepeat(item));
        command.Repeat.NotifyCanExecuteChanged();
        command.AsNew.NotifyCanExecuteChanged();
    }

    private async Task RepeatAsync(HistoryMessageListItem item, bool asNew)
    {
        if (!CanRepeat(item)) return;
        var owner = _supervisor.Snapshot;
        var node = owner.NodeId!.Value;
        var attemptNumber = item.Presentation.AttemptNumber!.Value;
        var selectedSlot = _workspace.Composer.SelectedSlot;
        _busy = true; Rebind();
        try
        {
            var stored = await _store!.GetAsync(node, item.Id, _token);
            var targets = await _service!.GetChannelTargetsAsync(node, stored.Recipient.Identity, _token);
            var target = targets.FirstOrDefault(target => target.Slot == selectedSlot) ??
                targets.FirstOrDefault(target => target.Slot == stored.Recipient.Slot) ?? targets.FirstOrDefault()
                ?? throw new InvalidOperationException("Channel is no longer configured.");
            var request = new ChannelRepeatRequest(node, owner.SessionId!.Value, owner.Generation, item.Id, attemptNumber, target);
            if (asNew) await _service.SendChannelAsNewAsync(request, _token);
            else await _service.RepeatChannelAsync(request, _token);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Could not repeat channel message {MessageId}", item.Id);
            await _dispatcher.InvokeAsync(() => _workspace.Composer.SetActionError("Не удалось повторить отправку. Проверьте статус сообщения и подключения."));
        }
        finally { _busy = false; if (!_stopped) Rebind(); }
    }
    public void Dispose()
    {
        _stopped = true;
        _workspace.Navigation.History.Messages.CollectionChanged -= OnCollectionChanged;
        _workspace.Composer.PropertyChanged -= OnContextChanged;
        _workspace.PropertyChanged -= OnContextChanged;
        _workspace.Navigation.PropertyChanged -= OnContextChanged;
        foreach (var item in _commands.Keys) item.PropertyChanged -= OnMessageChanged;
        _commands.Clear();
    }
}
