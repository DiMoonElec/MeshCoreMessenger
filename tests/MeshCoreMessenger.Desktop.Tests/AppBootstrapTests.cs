using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Bootstrap;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class AppBootstrapTests
{
    [Fact]
    public async Task BootstrapProvidesStorageViewModelAndLogging()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await using var storage = await LocalStorage.OpenAsync(paths, CancellationToken);
        using var services = AppBootstrap.CreateServiceProvider(paths, storage);

        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        var logger = services.GetRequiredService<ILogger<AppBootstrapTests>>();

        Assert.Equal(AppInformation.ProductName, viewModel.Title);
        Assert.NotNull(logger);
        Assert.Same(paths, services.GetRequiredService<IAppPaths>());
        Assert.Same(storage, services.GetRequiredService<LocalStorage>());
        Assert.Same(storage.History, services.GetRequiredService<ILocalHistoryReader>());
        Assert.Equal("Не подключено", viewModel.ConnectionStatus);
        await viewModel.StopAsync();
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "MeshCoreMessenger.Bootstrap.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public TestAppPaths CreatePaths() => new(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private sealed record TestAppPaths(string DataDirectory) : IAppPaths
    {
        public string DatabasePath { get; } = System.IO.Path.Combine(DataDirectory, "messenger.db");
        public string BackupsDirectory { get; } = System.IO.Path.Combine(DataDirectory, "backups");
    }
}
