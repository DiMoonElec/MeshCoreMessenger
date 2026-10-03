using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MeshCoreMessenger.Desktop.ViewModels;

public enum ConversationMenuAction { Details, Search, Share, SetRoute, ResetRoute, ClearHistory }

public sealed class ConversationMenuItemViewModel(
    ConversationMenuAction action, string header, ICommand command) : ObservableObject
{
    private string? _availabilityMessage;
    public ConversationMenuAction Action { get; } = action;
    public string Header { get; } = header;
    public ICommand Command { get; } = command;
    public string? AvailabilityMessage
    {
        get => _availabilityMessage;
        internal set => SetProperty(ref _availabilityMessage, value);
    }
}

public sealed class HistoryClearRequestedEventArgs(HistoryClearTarget target, HistoryClearViewModel operation) : EventArgs
{
    public HistoryClearTarget Target { get; } = target;
    public HistoryClearViewModel Operation { get; } = operation;
}

/// <summary>One fixed menu per workspace. Actions are wired at construction; targets are captured on invocation.</summary>
public sealed class ConversationMenuViewModel
{
    private HistoryClearViewModel _historyClear;
    private readonly RelayCommand _clear;
    private readonly ConversationMenuItemViewModel _clearItem;

    internal ConversationMenuViewModel(MessengerNavigationTab kind, HistoryClearViewModel historyClear)
    {
        _historyClear = historyClear;
        _clear = new RelayCommand(RequestHistoryClear, () => _historyClear.CanClear);
        _clearItem = new(ConversationMenuAction.ClearHistory, "Удалить историю сообщений", _clear);
        var items = new List<ConversationMenuItemViewModel>
        {
            Placeholder(ConversationMenuAction.Details, "Подробности (тест)"),
            Placeholder(ConversationMenuAction.Search, "Поиск (тест)"),
            Placeholder(ConversationMenuAction.Share, "Поделиться (тест)"),
        };
        if (kind == MessengerNavigationTab.Personal)
        {
            items.Add(Placeholder(ConversationMenuAction.SetRoute, "Задать маршрут (тест)"));
            items.Add(Placeholder(ConversationMenuAction.ResetRoute, "Сбросить маршрут (тест)"));
        }
        items.Add(_clearItem);
        Items = items.AsReadOnly();
        _historyClear.PropertyChanged += OnHistoryClearChanged;
        UpdateAvailability();
    }

    public IReadOnlyList<ConversationMenuItemViewModel> Items { get; }
    public event EventHandler<HistoryClearRequestedEventArgs>? HistoryClearRequested;
    public Task RefreshAsync() => _historyClear.RefreshAsync();

    internal void SetHistoryClear(HistoryClearViewModel historyClear)
    {
        _historyClear.PropertyChanged -= OnHistoryClearChanged;
        _historyClear = historyClear;
        _historyClear.PropertyChanged += OnHistoryClearChanged;
        UpdateAvailability();
    }

    private static ConversationMenuItemViewModel Placeholder(ConversationMenuAction action, string header) =>
        new(action, header, new RelayCommand(() => { }));

    private void RequestHistoryClear()
    {
        if (_historyClear.Capture() is { } target)
            HistoryClearRequested?.Invoke(this, new(target, _historyClear));
    }

    private void OnHistoryClearChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(HistoryClearViewModel.CanClear) or nameof(HistoryClearViewModel.AvailabilityMessage))
            UpdateAvailability();
    }

    private void UpdateAvailability()
    {
        _clearItem.AvailabilityMessage = _historyClear.AvailabilityMessage;
        _clear.NotifyCanExecuteChanged();
    }
}
