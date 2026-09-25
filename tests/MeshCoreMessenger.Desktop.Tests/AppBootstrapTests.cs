using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core;
using MeshCoreMessenger.Desktop.Bootstrap;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class AppBootstrapTests
{
    [Fact]
    public void BootstrapProvidesViewModelAndLogging()
    {
        using var services = AppBootstrap.CreateServiceProvider();

        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        var logger = services.GetRequiredService<ILogger<AppBootstrapTests>>();
        var paths = services.GetRequiredService<IAppPaths>();

        Assert.Equal(AppInformation.ProductName, viewModel.Title);
        Assert.NotNull(logger);
        Assert.True(Path.IsPathFullyQualified(paths.DatabasePath));
        Assert.Equal("messenger.db", Path.GetFileName(paths.DatabasePath));
    }
}
