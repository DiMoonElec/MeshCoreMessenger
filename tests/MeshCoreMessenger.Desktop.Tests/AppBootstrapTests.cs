using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Bootstrap;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Preferences;
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
        await using var services = AppBootstrap.CreateServiceProvider(paths, storage);

        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        var logger = services.GetRequiredService<ILogger<AppBootstrapTests>>();

        Assert.Equal("MeshCore Messenger - Отключено", viewModel.Title);
        Assert.NotNull(logger);
        Assert.Same(paths, services.GetRequiredService<IAppPaths>());
        Assert.Same(storage, services.GetRequiredService<LocalStorage>());
        Assert.Same(storage.History, services.GetRequiredService<ILocalHistoryReader>());
        Assert.Same(storage.ReadStates, services.GetRequiredService<IConversationReadStateStore>());
        Assert.Same(storage.Drafts, services.GetRequiredService<IDraftStore>());
        Assert.NotNull(services.GetRequiredService<IConnectionProfileManager>());
        Assert.NotNull(services.GetRequiredService<IMeshCoreClientFactory>());
        Assert.NotNull(services.GetRequiredService<ICompanionSessionFactory>());
        Assert.NotNull(services.GetRequiredService<DirectoryService>());
        Assert.NotNull(services.GetRequiredService<MessageIngestor>());
        Assert.Same(
            services.GetRequiredService<SessionCompletionTracker>(),
            services.GetRequiredService<IDurableSessionCompletion>());
        Assert.Same(
            services.GetRequiredService<ConversationReadStateTracker>(),
            services.GetRequiredService<IDurableReadStateWrites>());
        Assert.Same(
            services.GetRequiredService<DraftWriteTracker>(),
            services.GetRequiredService<IDraftBuffer>());
        Assert.Same(
            services.GetRequiredService<DraftWriteTracker>(),
            services.GetRequiredService<IDurableDraftWrites>());
        Assert.NotNull(services.GetRequiredService<IConnectionAttemptFactory>());
        Assert.NotNull(services.GetRequiredService<IConnectionFailureClassifier>());
        Assert.NotNull(services.GetRequiredService<IReconnectDelay>());
        Assert.NotNull(services.GetRequiredService<IReconnectJitter>());
        Assert.NotNull(services.GetRequiredService<IPlatformPowerEvents>());
        var supervisor = services.GetRequiredService<IConnectionSupervisor>();
        Assert.Same(supervisor, services.GetRequiredService<IConnectionSupervisor>());
        Assert.NotNull(services.GetRequiredService<DesktopConnectionLifecycle>());
        Assert.Same(
            services.GetRequiredService<DesktopConnectionLifecycle>(),
            services.GetRequiredService<IDesktopConnectionLifecycle>());
        Assert.Same(
            services.GetRequiredService<MessageIngestor>(),
            services.GetRequiredService<IDurableMessageIngress>());
        Assert.NotNull(services.GetRequiredService<IDesktopShutdownCoordinator>());
        Assert.NotNull(services.GetRequiredService<IUiDispatcher>());
        Assert.NotNull(services.GetRequiredService<ISearchDelay>());
        Assert.NotNull(services.GetRequiredService<IDraftDelay>());
        Assert.Same(
            services.GetRequiredService<DesktopPreferences>(),
            services.GetRequiredService<IDurableDesktopPreferences>());
        Assert.NotNull(services.GetRequiredService<IMessageCommitNotifications>());
        Assert.Null(services.GetService<ReceiveCoordinator>());
        Assert.Same(storage.Directories, services.GetRequiredService<IDirectoryStore>());
        Assert.Same(storage.IncomingMessages, services.GetRequiredService<IIncomingMessageStore>());
        Assert.NotNull(services.GetRequiredService<ISerialPortCatalog>());
        Assert.Same(
            services.GetRequiredService<ConnectionProfilesViewModel>(),
            viewModel.Profiles);
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
