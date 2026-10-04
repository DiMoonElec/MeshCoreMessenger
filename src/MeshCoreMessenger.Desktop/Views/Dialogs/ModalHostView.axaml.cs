using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop.Views.Dialogs;

/// <summary>Window-local overlay. It owns visual focus, keyboard isolation and responsive card bounds.</summary>
public sealed partial class ModalHostView : UserControl
{
    private ModalHostViewModel? _model;
    private TopLevel? _owner;
    private IInputElement? _previousFocus;
    private Control? _lastBackgroundFocus;
    private long _focusRevision;

    public ModalHostView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindModel();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == BoundsProperty)
            {
                Card.MaxHeight = Math.Max(0, Bounds.Height - 40);
                Card.MaxWidth = Math.Max(0, Math.Min(640, Bounds.Width - 40));
            }
        };
        AttachedToVisualTree += (_, _) =>
        {
            _owner = TopLevel.GetTopLevel(this);
            _owner?.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
            _owner?.AddHandler(GotFocusEvent, OnBackgroundGotFocus, RoutingStrategies.Tunnel);
            BindModel();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _owner?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
            _owner?.RemoveHandler(GotFocusEvent, OnBackgroundGotFocus);
            _lastBackgroundFocus = null;
            _owner = null;
            if (_model is not null) _model.PropertyChanged -= OnModelChanged;
            _model = null;
            ++_focusRevision;
        };
    }
    private void BindModel()
    {
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = DataContext as ModalHostViewModel;
        if (_model is null) return;
        _model.PropertyChanged += OnModelChanged;
        if (_model.IsOpen) MoveFocus(true);
    }
    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ModalHostViewModel.Active)) MoveFocus(_model!.IsOpen);
    }
    private void MoveFocus(bool open)
    {
        var revision = ++_focusRevision;
        if (open) _previousFocus = _owner?.FocusManager?.GetFocusedElement();
        Dispatcher.UIThread.Post(() =>
        {
            if (revision != _focusRevision || _owner is null) return;
            if (open) CloseButton.Focus();
            else
            {
                if (_previousFocus is Control { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } control)
                    control.Focus();
                else if (_lastBackgroundFocus is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } fallback)
                    fallback.Focus();
                _previousFocus = null;
            }
        }, DispatcherPriority.Loaded);
    }
    private void OnBackgroundGotFocus(object? sender, FocusChangedEventArgs args)
    {
        // Menu flyouts have their own visual root; remember the last focus inside the owner window.
        if (_model?.IsOpen != true && args.NewFocusedElement is Control control && TopLevel.GetTopLevel(control) == _owner)
            _lastBackgroundFocus = control;
    }
    private void OnWindowKeyDown(object? sender, KeyEventArgs args)
    {
        if (_model?.IsOpen != true || args.Key != Key.Escape) return;
        _model.Close(ModalCloseReason.Escape);
        args.Handled = true;
    }
    private void OnBackdropPressed(object? sender, PointerPressedEventArgs args)
    {
        _model?.Close(ModalCloseReason.Backdrop);
        args.Handled = true;
    }
}
