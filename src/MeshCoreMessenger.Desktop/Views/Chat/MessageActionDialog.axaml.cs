using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MeshCoreMessenger.Desktop.Views.Chat;

public sealed partial class MessageActionDialog : Window
{
    public MessageActionDialog() : this(string.Empty, false) { }
    public MessageActionDialog(string explanation, bool confirmation)
    {
        Explanation = explanation;
        IsConfirmation = confirmation;
        InitializeComponent();
        Title = confirmation ? "Повторить отправку?" : "Сведения о сообщении";
        DataContext = this;
    }
    public string Explanation { get; }
    public bool IsConfirmation { get; }
    public string CancelLabel => IsConfirmation ? "Отмена" : "Закрыть";
    private void OnCancel(object? sender, RoutedEventArgs args) => Close(false);
    private void OnConfirm(object? sender, RoutedEventArgs args) => Close(true);
}
