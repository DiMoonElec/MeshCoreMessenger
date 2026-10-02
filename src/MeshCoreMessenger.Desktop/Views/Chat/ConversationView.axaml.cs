using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop.Views.Chat;

/// <summary>Visual viewport adapter only: no storage, sessions or ownership of VM lifetime.</summary>
public sealed partial class ConversationView : UserControl
{
    private MainWindowViewModel? _owner;
    private Window? _window;
    private HistoryWindowViewModel? _history;
    private long _revision;
    private bool _attached;
    private bool _wasVisible;
    private bool _pendingScroll;
    private bool _viewportPosted;
    private (Guid? Node, string? Chat, long Sequence, double Y)? _anchor;

    public ConversationView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindOwner();
        HistoryList.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => ReportViewport());
        LayoutUpdated += (_, _) => OnLayout();
    }

    public void FocusSearch() { HistorySearch.Focus(); HistorySearch.SelectAll(); }
    public bool IsSearchFocused => HistorySearch.IsFocused;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        _attached = true;
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
        {
            _window.Activated += OnActivation;
            _window.Deactivated += OnActivation;
        }
        BindOwner();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        _attached = false;
        if (_window is not null)
        {
            _window.Activated -= OnActivation;
            _window.Deactivated -= OnActivation;
        }
        _window = null;
        UnbindOwner();
        _wasVisible = false;
        base.OnDetachedFromVisualTree(args);
    }

    private void UnbindOwner()
    {
        _revision++;
        _pendingScroll = false;
        if (_history is not null)
        {
            _history.ReportVisibleRange(null, null, false, false);
            _history.ScrollRequested -= OnScrollRequested;
            _history.Messages.CollectionChanged -= OnMessagesChanged;
        }
        if (_owner is not null)
        {
            _owner.PropertyChanged -= OnOwnerChanged;
            _owner.Navigation.PropertyChanged -= OnNavigationChanged;
        }
        _owner = null;
        _history = null;
    }

    private void BindOwner()
    {
        UnbindOwner();
        _anchor = null;
        if (!_attached || DataContext is not MainWindowViewModel owner)
            return;
        _owner = owner;
        _history = owner.Navigation.History;
        _history.ScrollRequested += OnScrollRequested;
        _history.Messages.CollectionChanged += OnMessagesChanged;
        owner.PropertyChanged += OnOwnerChanged;
        owner.Navigation.PropertyChanged += OnNavigationChanged;
        UpdateMessageVisibility();
        QueueViewport();
    }

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.ViewedNode))
            InvalidateContext();
    }

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ConversationNavigationViewModel.SelectedConversation))
            InvalidateContext();
    }

    private void InvalidateContext()
    {
        _revision++;
        _anchor = null;
        _pendingScroll = false;
        _history?.ReportVisibleRange(null, null, false, false);
        UpdateMessageVisibility();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        UpdateMessageVisibility();
        QueueViewport();
    }

    private void UpdateMessageVisibility() => HistoryList.IsVisible = _history?.Messages.FirstOrDefault() is { } first &&
        first.ConversationId == _owner?.Navigation.SelectedConversation?.Id;
    private void OnActivation(object? sender, EventArgs args) => ReportViewport();

    private void OnLayout()
    {
        var visible = _attached && IsEffectivelyVisible;
        if (visible != _wasVisible)
        {
            _revision++;
            _pendingScroll = false;
            _wasVisible = visible;
            if (visible)
            {
                var sequence = _anchor is { } anchor && anchor.Node == _owner?.ViewedNode?.Id &&
                    anchor.Chat == _owner?.Navigation.SelectedConversation?.StableKey ? anchor.Sequence : (long?)null;
                OnScrollRequested(_history, new HistoryScrollRequestEventArgs(sequence, sequence is null));
            }
            else
                _history?.ReportVisibleRange(null, null, false, false);
        }
        QueueViewport();
    }

    private void QueueViewport()
    {
        if (_viewportPosted || !_attached)
            return;
        _viewportPosted = true;
        Dispatcher.UIThread.Post(() => { _viewportPosted = false; ReportViewport(); }, DispatcherPriority.Background);
    }

    private void OnScrollRequested(object? sender, HistoryScrollRequestEventArgs args)
    {
        if (!_attached || !IsEffectivelyVisible || _history is null || !ReferenceEquals(sender, _history))
            return;
        var revision = _revision;
        var savedAnchor = _anchor;
        _pendingScroll = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (!CanApplyScroll(revision))
                return;
            var item = args.ScrollToEnd ? _history!.Messages.LastOrDefault() :
                _history!.Messages.FirstOrDefault(message => message.LocalSequence == args.AnchorSequence);
            if (item is not null)
                HistoryList.ScrollIntoView(item);
            Dispatcher.UIThread.Post(() =>
            {
                if (!CanApplyScroll(revision))
                    return;
                if (item is not null && !args.ScrollToEnd && savedAnchor is { } anchor && anchor.Sequence == item.LocalSequence)
                {
                    var container = HistoryList.ContainerFromIndex(HistoryList.Items.IndexOf(item));
                    var viewer = GetScrollViewer();
                    if (container?.TranslatePoint(default, HistoryList) is { } origin && viewer is not null)
                        viewer.Offset = new Vector(viewer.Offset.X, viewer.Offset.Y + origin.Y - anchor.Y);
                }
                _pendingScroll = false;
                QueueViewport();
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }

    private bool CanApplyScroll(long revision) => ChatViewportPolicy.CanApplyCallback(revision, _revision, _attached, IsEffectivelyVisible);
    private ScrollViewer? GetScrollViewer() => HistoryList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private void ReportViewport()
    {
        if (_history is null || _pendingScroll)
            return;
        if (!_attached || !IsEffectivelyVisible || _history.Messages.FirstOrDefault()?.ConversationId != _owner?.Navigation.SelectedConversation?.Id)
        {
            _history.ReportVisibleRange(null, null, false, false);
            return;
        }
        var containers = HistoryList.GetRealizedContainers()
            .Select(container => (Index: HistoryList.IndexFromContainer(container), Origin: container.TranslatePoint(default, HistoryList),
                Height: container.Bounds.Height, Message: container.DataContext as HistoryMessageListItem))
            .Where(item => item.Index >= 0 && item.Index < _history.Messages.Count && item.Origin is not null &&
                item.Message?.Id == _history.Messages[item.Index].Id).ToArray();
        var visible = ChatViewportPolicy.VisibleIndices(containers.Select(item =>
            new RealizedMessageBounds(item.Index, item.Origin!.Value.Y, item.Height)), HistoryList.Bounds.Height, _history.Messages.Count);
        var first = visible.Length > 0 ? _history.Messages[visible[0]].LocalSequence : (long?)null;
        var last = visible.Length > 0 ? _history.Messages[visible[^1]].LocalSequence : (long?)null;
        var viewer = GetScrollViewer();
        var atEnd = viewer is not null && viewer.Offset.Y + viewer.Viewport.Height >= viewer.Extent.Height - 2;
        if (first is { } sequence)
            _anchor = (_owner?.ViewedNode?.Id, _owner?.Navigation.SelectedConversation?.StableKey, sequence,
                containers.Single(item => item.Index == visible[0]).Origin!.Value.Y);
        _history.ReportVisibleRange(first, last, ChatViewportPolicy.CanReportRead(_attached, IsEffectivelyVisible, _window?.IsActive == true), atEnd);
        if (visible.Length > 0 && visible[0] <= 2 && _history.CanLoadOlder && _history.LoadOlderCommand.CanExecute(null))
            _history.LoadOlderCommand.Execute(null);
        if (atEnd && _history.CanLoadNewer && _history.LoadNewerCommand.CanExecute(null))
            _history.LoadNewerCommand.Execute(null);
    }

    private async void OnSearchSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_history is null || sender is not ListBox list || list.SelectedItem is not HistorySearchResultListItem result)
            return;
        var history = _history;
        var revision = _revision;
        try
        {
            if (await history.JumpToSearchResultAsync(result) && revision == _revision)
                history.DismissSearchResults();
        }
        catch (OperationCanceledException) { }
        finally { list.SelectedItem = null; }
    }
}
