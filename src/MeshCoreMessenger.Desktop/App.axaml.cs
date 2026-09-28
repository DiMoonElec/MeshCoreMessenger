using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Desktop.Lifecycle;
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
            desktop.MainWindow = window;
            var viewModel = Services.GetRequiredService<MainWindowViewModel>();
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
}
