using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.Lifecycle;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Owns one local-only editor while durable state remains in the shared draft buffer.</summary>
public sealed class DraftEditorViewModel : ObservableObject
{
    internal static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    private readonly IDraftBuffer _drafts;
    private readonly IDraftDelay _delay;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _tasksGate = new();
    private readonly HashSet<Task> _tasks = [];
    private DraftTarget? _target;
    private CancellationTokenSource? _debounceCancellation;
    private string _text = string.Empty;
    private string? _errorMessage;
    private long _revision;
    private long _contextVersion;
    private bool _isDirty;
    private bool _isSaving;
    private bool _suppressUpdate;
    private int _stopped;

    public DraftEditorViewModel(
        IDraftBuffer drafts,
        IDraftDelay delay,
        IUiDispatcher dispatcher,
        ILogger logger)
    {
        _drafts = drafts;
        _delay = delay;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public string Text
    {
        get => _text;
        set
        {
            if (!SetProperty(ref _text, value ?? string.Empty) || _suppressUpdate || _target is null)
            {
                return;
            }

            var revision = Interlocked.Increment(ref _revision);
            _drafts.Update(_target, _text, revision);
            IsDirty = true;
            ErrorMessage = null;
            QueueSave(_target, revision, Volatile.Read(ref _contextVersion));
        }
    }

    public bool CanEdit => _target is not null;
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(Status));
            }
        }
    }
    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
            {
                OnPropertyChanged(nameof(Status));
            }
        }
    }
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(Status));
            }
        }
    }
    public bool HasError => ErrorMessage is not null;
    public string Status => ErrorMessage is not null
        ? string.Empty
        : IsSaving
            ? "Сохранение черновика…"
            : IsDirty
                ? "Черновик не сохранён"
                : string.Empty;

    public async Task OpenAsync(
        ConversationListItem? conversation,
        CancellationToken cancellationToken = default,
        bool dispatchResult = true)
    {
        var nextTarget = CreateTarget(conversation);
        var previous = _target;
        CancelDebounce();
        if (previous is not null && !SameOwner(previous, nextTarget))
        {
            await TryFlushAsync(previous, cancellationToken).ConfigureAwait(false);
        }

        var version = Interlocked.Increment(ref _contextVersion);
        // This publication also refreshes UI command availability after the asynchronous flush.
        await ApplyAsync(
            () =>
            {
                if (version != Volatile.Read(ref _contextVersion)) return;
                _target = nextTarget;
                OnPropertyChanged(nameof(CanEdit));
            },
            dispatchResult,
            cancellationToken);
        if (version != Volatile.Read(ref _contextVersion)) return;
        if (nextTarget is null)
        {
            await ApplyAsync(() => SetLoadedText(string.Empty), dispatchResult, cancellationToken);
            return;
        }

        var openingRevision = Volatile.Read(ref _revision);
        var text = await _drafts.LoadTextAsync(nextTarget, cancellationToken).ConfigureAwait(false);
        await ApplyAsync(
            () =>
            {
                if (version != Volatile.Read(ref _contextVersion) ||
                    openingRevision != Volatile.Read(ref _revision) ||
                    !SameOwner(nextTarget, _target))
                {
                    return;
                }

                SetLoadedText(text);
            },
            dispatchResult,
            cancellationToken);
    }

    public DraftCapture Capture() => _target is null
        ? throw new InvalidOperationException("No draft target.")
        : new(_target with { Identity = _target.Identity.ToArray() }, Text, Volatile.Read(ref _revision));

    public void AcceptTransfer(DraftCapture capture)
    {
        if (!SameOwner(capture.Target, _target) || capture.Revision != Volatile.Read(ref _revision) || Text != capture.Text) return;
        CancelDebounce();
        SetLoadedText(string.Empty);
    }

    public void Clear()
    {
        var previous = _target;
        CancelDebounce();
        Interlocked.Increment(ref _contextVersion);
        _target = null;
        OnPropertyChanged(nameof(CanEdit));
        SetLoadedText(string.Empty);
        if (previous is not null)
        {
            Track(TryFlushAsync(previous, CancellationToken.None));
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        CancelDebounce();
        _lifetimeCancellation.Cancel();
        Task[] tasks;
        lock (_tasksGate)
        {
            tasks = [.. _tasks];
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void QueueSave(DraftTarget target, long revision, long contextVersion)
    {
        CancelDebounce();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _debounceCancellation = cancellation;
        Track(SaveAfterDelayAsync(target, revision, contextVersion, cancellation));
    }

    private async Task SaveAfterDelayAsync(
        DraftTarget target,
        long revision,
        long contextVersion,
        CancellationTokenSource cancellation)
    {
        try
        {
            await _delay.DelayAsync(DebounceDelay, cancellation.Token).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (IsCurrent(target, contextVersion))
                    {
                        IsSaving = true;
                    }
                },
                cancellation.Token);
            await _drafts.FlushAsync(target, cancellation.Token).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (IsCurrent(target, contextVersion))
                    {
                        IsSaving = false;
                        if (revision == Volatile.Read(ref _revision))
                        {
                            IsDirty = false;
                        }
                        ErrorMessage = null;
                    }
                },
                cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not persist a local draft.");
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (IsCurrent(target, contextVersion))
                    {
                        IsSaving = false;
                        IsDirty = true;
                        ErrorMessage = "Не удалось сохранить черновик. Повтор будет выполнен при закрытии.";
                    }
                },
                CancellationToken.None);
        }
        finally
        {
            if (ReferenceEquals(_debounceCancellation, cancellation))
            {
                _debounceCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task TryFlushAsync(DraftTarget target, CancellationToken cancellationToken)
    {
        try
        {
            await _drafts.FlushAsync(target, cancellationToken).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (SameOwner(target, _target))
                    {
                        IsDirty = false;
                        IsSaving = false;
                    }
                    ErrorMessage = null;
                },
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not flush a local draft while changing context.");
            await _dispatcher.InvokeAsync(
                () => ErrorMessage =
                    "Не удалось сохранить черновик. Текст сохранён в памяти для повторной попытки.",
                CancellationToken.None);
        }
    }

    private void SetLoadedText(string text)
    {
        _suppressUpdate = true;
        try
        {
            Text = text;
            IsDirty = false;
            IsSaving = false;
        }
        finally
        {
            _suppressUpdate = false;
        }
    }

    private void CancelDebounce()
    {
        _debounceCancellation?.Cancel();
        _debounceCancellation = null;
    }

    private void Track(Task task)
    {
        lock (_tasksGate)
        {
            _tasks.Add(task);
        }
        _ = task.ContinueWith(
            completed =>
            {
                lock (_tasksGate)
                {
                    _tasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private Task ApplyAsync(Action action, bool dispatch, CancellationToken cancellationToken)
    {
        if (dispatch)
        {
            return _dispatcher.InvokeAsync(action, cancellationToken);
        }

        action();
        return Task.CompletedTask;
    }

    private bool IsCurrent(DraftTarget target, long contextVersion) =>
        Volatile.Read(ref _stopped) == 0 &&
        contextVersion == Volatile.Read(ref _contextVersion) &&
        SameOwner(target, _target);

    private static DraftTarget? CreateTarget(ConversationListItem? conversation) =>
        conversation?.Section switch
        {
            ConversationDirectorySection.ChatContacts => new DraftTarget(
                conversation.NodeId,
                conversation.Id,
                ConversationKind.Contact,
                conversation.Entry.Identity.ToArray()),
            ConversationDirectorySection.Channels => new DraftTarget(
                conversation.NodeId,
                conversation.Id,
                ConversationKind.Channel,
                conversation.Entry.Identity.ToArray()),
            _ => null,
        };

    private static bool SameOwner(DraftTarget? left, DraftTarget? right) =>
        ReferenceEquals(left, right) ||
        (left is not null && right is not null &&
         left.NodeId == right.NodeId &&
         left.Kind == right.Kind &&
         left.Identity.AsSpan().SequenceEqual(right.Identity));
}
