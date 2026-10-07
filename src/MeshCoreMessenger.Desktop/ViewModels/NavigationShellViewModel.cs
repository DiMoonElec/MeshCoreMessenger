using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MeshCoreMessenger.Desktop.ViewModels;

public enum ShellSection
{
    PublicChats,
    PrivateChats,
    Devices,
    Connection,
    Settings,
}

/// <summary>Application navigation is independent of node-scoped conversation selection.</summary>
public sealed class NavigationShellViewModel : ObservableObject
{
    private NavigationShellItem _selectedItem;
    private ShellSection _settingsReturnSection = ShellSection.PublicChats;

    public NavigationShellViewModel()
    {
        TopItems =
        [
            new(ShellSection.PublicChats, "Каналы", ShellIconAssets.Chats, Select),
            new(ShellSection.PrivateChats, "Чаты", ShellIconAssets.Private, Select),
            new(ShellSection.Devices, "Устройства", ShellIconAssets.Devices, Select),
        ];
        BottomItems =
        [
            new(ShellSection.Connection, "Подключение", ShellIconAssets.Connection, Select),
            new(ShellSection.Settings, "Настройки", ShellIconAssets.Settings, Select),
        ];
        _selectedItem = TopItems[0];
        _selectedItem.IsSelected = true;
        ReturnFromSettingsCommand = new RelayCommand(() => SelectSection(_settingsReturnSection),
            () => SelectedItem.Section == ShellSection.Settings);
    }

    public IReadOnlyList<NavigationShellItem> TopItems { get; }
    public IReadOnlyList<NavigationShellItem> BottomItems { get; }

    public NavigationShellItem SelectedItem
    {
        get => _selectedItem;
        private set
        {
            if (SetProperty(ref _selectedItem, value))
                OnPropertyChanged(nameof(IsChatSelected));
        }
    }

    public bool IsChatSelected => SelectedItem.Section is ShellSection.PublicChats or ShellSection.PrivateChats;
    public IRelayCommand ReturnFromSettingsCommand { get; }

    public void SelectSection(ShellSection section) => Select(TopItems.Concat(BottomItems).Single(item => item.Section == section));

    private void Select(NavigationShellItem item)
    {
        if (ReferenceEquals(item, SelectedItem))
        {
            return;
        }

        if (item.Section == ShellSection.Settings) _settingsReturnSection = SelectedItem.Section;
        SelectedItem.IsSelected = false;
        item.IsSelected = true;
        SelectedItem = item;
        ReturnFromSettingsCommand.NotifyCanExecuteChanged();
    }
}

public sealed class NavigationShellItem : ObservableObject
{
    private bool _isSelected;

    internal NavigationShellItem(
        ShellSection section,
        string title,
        string iconUri,
        Action<NavigationShellItem> select)
    {
        Section = section;
        Title = title;
        IconUri = iconUri;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public ShellSection Section { get; }
    public string Title { get; }

    /// <summary>avares:// URI of the PNG icon; the view layer turns it into a bitmap.</summary>
    public string IconUri { get; }

    public IRelayCommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}
