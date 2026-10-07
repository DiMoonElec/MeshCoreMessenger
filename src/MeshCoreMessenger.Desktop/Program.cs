using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Bootstrap;
using MeshCoreMessenger.Desktop.DevFixtures;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop;

internal static class Program
{
    private const int AlreadyRunningExitCode = 2;
    private const int DataDirectoryUnavailableExitCode = 3;
    private const int LocalStorageUnavailableExitCode = 4;
    private const int FakeDataSeedUnavailableExitCode = 5;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
#if !DEBUG
            if (args.Contains("--seed-fake-data"))
                Console.Error.WriteLine("--seed-fake-data is ignored in Release builds; starting normally without seeding.");
#endif
            DesktopAppPaths paths;
            var defaultPaths = DesktopAppPaths.CreateDefault();
            try
            {
                paths = DesktopAppPaths.CreateForDirectory(DataDirectorySelection.Resolve(
                    args, Environment.GetEnvironmentVariable("MESHCORE_DATA_DIR"),
                    defaultPaths.DataDirectory, Environment.CurrentDirectory));
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                Console.Error.WriteLine($"MeshCoreMessenger cannot select data directory: {exception.Message}");
                return DataDirectoryUnavailableExitCode;
            }
#if DEBUG
            var seed = FakeDataSeedOptions.Parse(args, paths.DataDirectory, defaultPaths.DataDirectory);
            if (seed is not null)
                FakeDataSeeder.ValidateDirectoryTarget(paths.DataDirectory, defaultPaths.DataDirectory);
#endif
            using var activation = new DesktopActivationCoordinator(new AvaloniaUiDispatcher());
            using var instance = ApplicationInstanceCoordinator.AcquireOrActivateAsync(paths, activation).GetAwaiter().GetResult();
            if (instance is null) return 0;
            LocalStorage storage;
            try
            {
#if DEBUG
                if (seed is not null)
                    FakeDataSeeder.EnsureEmptyDatabaseAsync(paths).GetAwaiter().GetResult();
#endif
                storage = LocalStorage.OpenAsync(paths).GetAwaiter().GetResult();
            }
            catch (Exception exception) when (IsLocalStorageOpenError(exception))
            {
                Console.Error.WriteLine($"MeshCoreMessenger could not open local storage: {exception.Message}");
                return LocalStorageUnavailableExitCode;
            }

            try
            {
#if DEBUG
                if (seed is not null)
                {
                    try
                    {
                        var result = new FakeDataSeeder().SeedAsync(storage, paths, seed.Large, DateTimeOffset.UtcNow).GetAwaiter().GetResult();
                        Console.WriteLine($"Fake data ready: {result.ChannelCount} channels, {result.ContactCount} contacts, {result.MessageCount} messages. No connection will be started.");
                    }
                    catch (Exception exception) when (IsLocalStorageOpenError(exception))
                    {
                        Console.Error.WriteLine($"Fake-data seed failed: {exception.Message}. No UI/connection started; the partial fixture is not retried automatically. Use a new test folder.");
                        return FakeDataSeedUnavailableExitCode;
                    }
                }
#endif
                var services = AppBootstrap.CreateServiceProvider(paths, storage, activation);
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
        catch (ApplicationActivationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return AlreadyRunningExitCode;
        }
        catch (ApplicationDataDirectoryUnavailableException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return DataDirectoryUnavailableExitCode;
        }
        catch (FakeDataSeedException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return FakeDataSeedUnavailableExitCode;
        }
    }

    private static bool IsLocalStorageOpenError(Exception exception) =>
        exception is DatabaseStorageException or IOException or UnauthorizedAccessException;

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
