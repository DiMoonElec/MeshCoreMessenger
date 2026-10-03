using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop.Views.Chat;

public sealed partial class ComposerView : UserControl
{
    public ComposerView()
    {
        InitializeComponent();
        MessageInput.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
    }

    internal static bool IsSendGesture(Key key, KeyModifiers modifiers, bool composing) =>
        key == Key.Enter && modifiers == KeyModifiers.None && !composing;

    private void OnInputKeyDown(object? sender, KeyEventArgs args)
    {
        var composing = MessageInput.GetVisualDescendants().OfType<TextPresenter>()
            .Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText));
        if (!IsSendGesture(args.Key, args.KeyModifiers, composing)) return;
        args.Handled = true;
        if (DataContext is ComposerViewModel composer && composer.SendCommand.CanExecute(null))
            composer.SendCommand.Execute(null);
    }
}
