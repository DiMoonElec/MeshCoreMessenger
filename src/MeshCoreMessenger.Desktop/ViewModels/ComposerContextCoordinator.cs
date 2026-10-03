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
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _pending = [];
    private long _revision;
    private bool _stopped;
    private bool _initialized;

    public ComposerContextCoordinator(ChatWorkspaceViewModel workspace, IConnectionSupervisor supervisor,
        ISendReadinessReader? reader, IUiDispatcher dispatcher, ILogger logger)
    {
        _workspace = workspace;
        _supervisor = supervisor;
        _reader = reader;
        _dispatcher = dispatcher;
        _logger = logger;
        workspace.PropertyChanged += OnWorkspaceChanged;
        workspace.Navigation.PropertyChanged += OnNavigationChanged;
        Refresh();
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
            if (entry is not null)
                recipient = new(entry.Kind, entry.Identity.ToArray());
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
            void Apply()
            {
                if (IsCurrent(revision)) _workspace.Composer.Readiness = readiness;
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
        _workspace.PropertyChanged -= OnWorkspaceChanged;
        _workspace.Navigation.PropertyChanged -= OnNavigationChanged;
        _stop.Cancel();
        await Task.WhenAll(pending).ConfigureAwait(false);
        _stop.Dispose();
    }
}
