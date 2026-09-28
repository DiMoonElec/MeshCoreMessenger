using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Bootstrap;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop;

internal static class Program
{
    private const int AlreadyRunningExitCode = 2;
    private const int DataDirectoryUnavailableExitCode = 3;
    private const int LocalStorageUnavailableExitCode = 4;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var paths = DesktopAppPaths.CreateDefault();
            using var instanceLock = ApplicationInstanceLock.Acquire(paths);
            LocalStorage storage;
            try
            {
                storage = LocalStorage.OpenAsync(paths).GetAwaiter().GetResult();
            }
            catch (Exception exception) when (IsLocalStorageOpenError(exception))
            {
                Console.Error.WriteLine($"MeshCoreMessenger could not open local storage: {exception.Message}");
                return LocalStorageUnavailableExitCode;
            }

            try
            {
                var services = AppBootstrap.CreateServiceProvider(paths, storage);
                try
                {
                    var viewModel = services.GetRequiredService<MainWindowViewModel>();
                    viewModel.LoadAsync().GetAwaiter().GetResult();
                    var shutdown = services.GetRequiredService<DesktopShutdownCoordinator>();
                    App.Services = services;
                    try
                    {
                        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                    }
                    finally
                    {
                        try
                        {
                            shutdown.ShutdownForProcessExitAsync().GetAwaiter().GetResult();
                        }
                        catch (DesktopShutdownException exception)
                        {
                            // A forced OS/process shutdown cannot keep the UI alive for retry.
                            Console.Error.WriteLine(exception.Message);
                        }
                    }
                }
                finally
                {
                    services.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            finally
            {
                storage.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
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

    private static bool IsLocalStorageOpenError(Exception exception) =>
        exception is DatabaseStorageException or IOException or UnauthorizedAccessException;

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
