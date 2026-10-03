using System.ComponentModel;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.Lifecycle;
using Microsoft.Extensions.Logging;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Tracks read-only composer context without exposing the live client. Owns pending reads until shutdown.</summary>
internal sealed class ComposerContextCoordinator
{
    private readonly ChatWorkspaceViewModel _workspace;
    private readonly IConnectionSupervisor _supervisor;
    private readonly ISendReadinessReader? _reader;
    private readonly IMessageService? _messages;
    private string? _recipientKey;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _pending = [];
    private long _revision;
    private bool _stopped;
    private bool _initialized;

    public ComposerContextCoordinator(ChatWorkspaceViewModel workspace, IConnectionSupervisor supervisor,
        ISendReadinessReader? reader, IUiDispatcher dispatcher, ILogger logger, IMessageService? messages = null)
    {
        _workspace = workspace;
        _supervisor = supervisor;
        _reader = reader;
        _messages = messages;
        if (messages is not null) workspace.Composer.ConfigureSend(SendAsync);
        workspace.Composer.PropertyChanged += OnComposerChanged;
        _dispatcher = dispatcher;
        _logger = logger;
        workspace.PropertyChanged += OnWorkspaceChanged;
        workspace.Navigation.PropertyChanged += OnNavigationChanged;
        Refresh();
    }

    private async Task SendAsync()
    {
        var snapshot = _workspace.Composer.SendCapture ?? throw new InvalidOperationException("No send target.");
        var request = snapshot with { Draft = _workspace.Composer.Draft!.Capture(), Options = _workspace.Composer.Options };
        Task Transferred(DraftCapture capture) => _dispatcher.InvokeAsync(
            () => _workspace.Composer.Draft!.AcceptTransfer(capture), CancellationToken.None);
        switch (request)
        {
            case ChannelSendRequest channel:
                await _messages!.SendChannelAsync(channel, Transferred, _stop.Token);
                break;
            case PrivateSendRequest personal:
                await _messages!.SendPrivateAsync(personal, Transferred, _stop.Token);
                break;
            default: throw new InvalidOperationException("Unsupported send target.");
        }
    }

    private void OnComposerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ComposerViewModel.SelectedSlot) or nameof(ComposerViewModel.CanEdit)) Refresh();
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ChatWorkspaceViewModel.ViewedNode)) Refresh();
    }
    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ConversationNavigationViewModel.SelectedConversation)) Refresh();
    }
    // Called by the existing root presentation publication, already on the UI dispatcher.
    public void Refresh()
    {
        lock (_gate)
        {
            if (_stopped) return;
            Track(RefreshAsync(++_revision, dispatchResult: true, readDirectory: _initialized));
        }
    }

    // Offline startup precedes Avalonia's event loop. Its initial projection must not queue UI work.
    public async Task InitializeAsync()
    {
        Task initial;
        lock (_gate)
        {
            if (_stopped) return;
            initial = Track(RefreshAsync(++_revision, dispatchResult: false, readDirectory: true));
        }
        await initial;
        lock (_gate) { if (!_stopped) _initialized = true; }
    }

    private Task Track(Task task)
    {
        _pending.Add(task);
        _ = task.ContinueWith(completed => { lock (_gate) _pending.Remove(completed); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private async Task RefreshAsync(long revision, bool dispatchResult, bool readDirectory)
    {
        ConnectionSupervisorSnapshot? connection = null;
        SendRecipient? recipient = null;
        Guid? node = null;
        try
        {
            if (!IsCurrent(revision)) return;
            connection = _supervisor.Snapshot;
            node = _workspace.ViewedNode?.Id;
            var entry = _workspace.SelectedConversation?.Entry;
            var recipientKey = entry is null ? null : $"{node}:{Convert.ToHexString(entry.Identity)}";
            if (_recipientKey != recipientKey)
            {
                _recipientKey = recipientKey;
                _workspace.Composer.ResetSendContext(clearSlots: true);
            }
            else _workspace.Composer.ResetSendContext();
            if (entry is not null)
                recipient = new(entry.Kind, entry.Identity.ToArray(), _workspace.Composer.SelectedSlot);
            var isChannel = entry?.Kind is ConversationKind.Channel or ConversationKind.UnknownChannel ||
                (entry is null && _workspace.Navigation.SelectedTab.Tab == MessengerNavigationTab.Channels);
            var senderName = connection.State == ConnectionSupervisorState.Online && connection.NodeId == node && connection.SessionId is not null
                ? connection.SenderName : null;
            _workspace.Composer.Context = new(isChannel, senderName);
            var unavailable = SendReadinessReader.CheckContext(connection, node, recipient);
            _workspace.Composer.Readiness = unavailable ?? SendReadiness.Checking;
            if (unavailable is not null || !readDirectory) return;
            if (connection is null || !IsCurrent(revision)) return;
            var readiness = _reader is not null
                ? await _reader.ReadAsync(connection, node, recipient, _stop.Token).ConfigureAwait(false)
                : SendReadinessReader.CheckContext(connection, node, recipient) ?? SendReadiness.ReadFailed;
            var targets = _messages is not null && recipient?.Kind == ConversationKind.Channel
                ? await _messages.GetChannelTargetsAsync(node!.Value, recipient.Identity, _stop.Token).ConfigureAwait(false)
                : [];
            void Apply()
            {
                if (!IsCurrent(revision)) return;
                _workspace.Composer.Readiness = readiness;
                if (targets.Count > 0) _workspace.Composer.SetSendContext(node!.Value, connection.SessionId!.Value, connection.Generation, targets);
                else if (_messages is not null && readiness == SendReadiness.Ready && recipient?.Kind == ConversationKind.Contact)
                    _workspace.Composer.SetPrivateSendContext(node!.Value, connection.SessionId!.Value, connection.Generation, recipient.Identity);
            }
            if (dispatchResult) await _dispatcher.InvokeAsync(Apply, _stop.Token);
            else Apply();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger.LogError(error, "Could not read composer recipient readiness.");
            void ApplyError()
            {
                if (IsCurrent(revision)) _workspace.Composer.Readiness = SendReadiness.ReadFailed;
            }
            if (dispatchResult) await _dispatcher.InvokeAsync(ApplyError, CancellationToken.None);
            else ApplyError();
        }
    }

    private bool IsCurrent(long revision) { lock (_gate) return !_stopped && _revision == revision; }

    public async Task StopAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _revision++;
            pending = [.. _pending];
        }
        _workspace.Composer.StopSend();
        _workspace.Composer.PropertyChanged -= OnComposerChanged;
        _workspace.PropertyChanged -= OnWorkspaceChanged;
        _workspace.Navigation.PropertyChanged -= OnNavigationChanged;
        _stop.Cancel();
        await Task.WhenAll(pending).ConfigureAwait(false);
        if (_workspace.Composer.SendCommand.ExecutionTask is { } send) await send.ConfigureAwait(false);
        _stop.Dispose();
    }
}
