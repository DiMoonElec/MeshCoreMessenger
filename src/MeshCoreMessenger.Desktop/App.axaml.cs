using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreMessenger.Desktop.Preferences;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreMessenger.Desktop.Views;

namespace MeshCoreMessenger.Desktop;

public sealed partial class App : Application
{
    internal static IServiceProvider Services { private get; set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var logger = Services.GetRequiredService<ILogger<App>>();
            var window = Services.GetRequiredService<MainWindow>();
            window.DataDirectoryTitleSuffix = DataDirectorySelection.WindowTitleSuffix(
                Services.GetRequiredService<IAppPaths>().DataDirectory,
                DesktopAppPaths.CreateDefault().DataDirectory);
            desktop.MainWindow = window;
            var viewModel = Services.GetRequiredService<MainWindowViewModel>();
            RequestedThemeVariant = ToThemeVariant(viewModel.SelectedTheme.Value);
            var connectionLifecycle = Services.GetRequiredService<DesktopConnectionLifecycle>();
            EventHandler? opened = null;
            opened = async (_, _) =>
            {
                window.Opened -= opened;
                try
                {
                    await connectionLifecycle.StartAsync();
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Could not start the Desktop connection lifecycle.");
                }
            };
            window.Opened += opened;
            logger.LogInformation(
                "MeshCoreMessenger desktop shell started. Local history state: {HistoryStatus}",
                viewModel.Status);
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static Avalonia.Styling.ThemeVariant ToThemeVariant(DesktopThemePreference theme) =>
        theme switch
        {
            DesktopThemePreference.Light => Avalonia.Styling.ThemeVariant.Light,
            DesktopThemePreference.Dark => Avalonia.Styling.ThemeVariant.Dark,
            _ => Avalonia.Styling.ThemeVariant.Default,
        };
}
