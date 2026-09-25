using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            desktop.MainWindow = Services.GetRequiredService<MainWindow>();
            logger.LogInformation("MeshCoreMessenger desktop shell started.");
        }

        base.OnFrameworkInitializationCompleted();
    }
}
