using Avalonia.Controls;
using Avalonia.Interactivity;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop.Views.Chat;

public sealed partial class HistoryClearDialog : Window
{
    public HistoryClearDialog() : this(new(Guid.Empty, Guid.Empty, "", "")) { }
    public HistoryClearDialog(HistoryClearTarget target)
    {
        Explanation = $"Нода: {target.NodeTitle}\nПереписка: {target.ConversationTitle}\n\nУдалить всю локальную историю этой переписки и сведения о попытках отправки?\n\nКонтакт или канал и черновик сохранятся. Новые входящие сообщения могут появиться после очистки. Отменить удаление нельзя.";
        InitializeComponent();
        DataContext = this;
        Opened += (_, _) => CancelButton.Focus();
    }
    public string Explanation { get; }
    private void OnCancel(object? sender, RoutedEventArgs args) => Close(false);
    private void OnConfirm(object? sender, RoutedEventArgs args) => Close(true);
}
