using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreMessenger.Desktop.Views;

namespace MeshCoreMessenger.Desktop.Bootstrap;

public static class AppBootstrap
{
    public static ServiceProvider CreateServiceProvider(IAppPaths paths, LocalStorage storage)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(storage);
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
        });
        services.AddSingleton(paths);
        services.AddSingleton(storage);
        services.AddSingleton(storage.Settings);
        services.AddSingleton(storage.ConnectionProfiles);
        services.AddSingleton(storage.Nodes);
        services.AddSingleton(storage.Sessions);
        services.AddSingleton(storage.Directories);
        services.AddSingleton(storage.History);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConnectionProfileManager, ConnectionProfileManager>();
        services.AddSingleton<IMeshCoreClientFactory, MeshCoreClientFactory>();
        services.AddSingleton<ICompanionSessionFactory, CompanionSessionFactory>();
        services.AddSingleton<DirectoryService>();
        services.AddSingleton<ISerialPortCatalog, SystemSerialPortCatalog>();
        services.AddSingleton<ConnectionProfilesViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient(provider => new MainWindow
        {
            DataContext = provider.GetRequiredService<MainWindowViewModel>(),
        });

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
