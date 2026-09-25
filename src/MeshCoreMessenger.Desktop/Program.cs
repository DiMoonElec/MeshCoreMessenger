using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Desktop.Bootstrap;
using MeshCoreMessenger.Desktop.Platform;

namespace MeshCoreMessenger.Desktop;

internal static class Program
{
    private const int AlreadyRunningExitCode = 2;
    private const int DataDirectoryUnavailableExitCode = 3;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            using var services = AppBootstrap.CreateServiceProvider();
            var paths = services.GetRequiredService<IAppPaths>();
            using var instanceLock = ApplicationInstanceLock.Acquire(paths);
            App.Services = services;
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (ApplicationInstanceAlreadyRunningException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return AlreadyRunningExitCode;
        }
        catch (ApplicationDataDirectoryUnavailableException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return DataDirectoryUnavailableExitCode;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
