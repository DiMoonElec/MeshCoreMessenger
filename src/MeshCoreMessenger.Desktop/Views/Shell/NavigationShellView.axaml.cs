using Avalonia.Controls;
using Avalonia;
using System.ComponentModel;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop.Views.Shell;

public sealed partial class NavigationShellView : UserControl
{
    private readonly Dictionary<ShellSection, (Control View, Func<bool>? Ready)> _contents = [];
    private NavigationShellViewModel? _owner;
    public NavigationShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_owner is not null) _owner.PropertyChanged -= OnSelectionChanged;
            _owner = DataContext as NavigationShellViewModel;
            if (_owner is not null) _owner.PropertyChanged += OnSelectionChanged;
            RefreshContent();
        };
    }
    // All sections use the same retained-content mechanism; shared views are added only once.
    public void RegisterContent(ShellSection section, Control view, Func<bool>? ready = null)
    {
        if (!_contents.Values.Any(item => ReferenceEquals(item.View, view)))
        {
            view.IsVisible = false;
            ContentHost.Children.Add(view);
        }
        _contents[section] = (view, ready);
        RefreshContent();
    }
    public void RefreshContent()
    {
        var selected = _owner is not null && _contents.TryGetValue(_owner.SelectedItem.Section, out var entry)
            ? entry : default;
        foreach (var view in _contents.Values.Select(item => item.View).Distinct())
            view.IsVisible = ReferenceEquals(view, selected.View) && (selected.Ready?.Invoke() ?? true);
        Placeholder.IsVisible = selected.View is null;
    }
    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(NavigationShellViewModel.SelectedItem)) RefreshContent();
    }
}
