using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using MeshCoreMessenger.Desktop.Presentation;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop.Views.Chat;

public sealed partial class MessageView : UserControl
{
    public static readonly StyledProperty<string?> OwnNodeNameProperty =
        AvaloniaProperty.Register<MessageView, string?>(nameof(OwnNodeName));
    public string? OwnNodeName { get => GetValue(OwnNodeNameProperty); set => SetValue(OwnNodeNameProperty, value); }
    public MessageView() => InitializeComponent();

    private async void OnCopyMessage(object? sender, RoutedEventArgs args)
    {
        if (sender is not MenuItem { CommandParameter: HistoryMessageListItem message } ||
            TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await MessageCopy.CopyAsync(message, text => clipboard.SetValueAsync(DataFormat.Text, text)); }
        catch (Exception error) { System.Diagnostics.Trace.TraceError("Could not copy message: {0}", error); }
    }

    private async void OnDetails(object? sender, RoutedEventArgs args)
    {
        if (sender is not MenuItem { CommandParameter: HistoryMessageListItem message } ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        await new MessageActionDialog(message.DetailsText, false) { RequestedThemeVariant = owner.ActualThemeVariant }.ShowDialog<bool>(owner);
    }

    private async void OnRetry(object? sender, RoutedEventArgs args)
    {
        if (sender is not MenuItem { CommandParameter: HistoryMessageListItem message } ||
            !message.CanRetry) return;
        if (!message.RetryRequiresConfirmation) { message.RequestRetry(); return; }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        // Capture the clicked message, not the current selection. Recheck admission after confirmation.
        if (await new MessageActionDialog("Сообщение могло быть доставлено. Повтор может создать дубликат у получателя.", true)
            { RequestedThemeVariant = owner.ActualThemeVariant }
            .ShowDialog<bool>(owner)) message.RequestRetry();
    }
}
