using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using MeshCoreMessenger.Desktop.Bootstrap;

namespace MeshCoreMessenger.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var services = AppBootstrap.CreateServiceProvider();
        App.Services = services;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
