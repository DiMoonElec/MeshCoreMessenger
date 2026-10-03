using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace MeshCoreMessenger.SendUiPreview;

public sealed partial class PreviewWindow : Window
{
    public PreviewWindow() => InitializeComponent();
    private void OnLight(object? sender, RoutedEventArgs args) => RequestedThemeVariant = ThemeVariant.Light;
    private void OnDark(object? sender, RoutedEventArgs args) => RequestedThemeVariant = ThemeVariant.Dark;
}
