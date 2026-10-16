using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Bootstrap;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Preferences;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Transport.Ble;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class AppBootstrapTests
{
    [Fact]
    public async Task SavedBleProfileRestoresBeforeUiLoopWithoutCreatingDispatcher()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await using var storage = await LocalStorage.OpenAsync(paths, CancellationToken);
        var dispatcher = new BootstrapDispatcher();
        await using var services = AppBootstrap.CreateServiceProvider(paths, storage, null, uiDispatcher: dispatcher);
        var manager = services.GetRequiredService<IConnectionProfileManager>();
        var saved = await manager.SaveAndSelectAsync(new()
        {
            Name = "Restart BLE", Transport = ConnectionTransportKind.Ble,
            BleDeviceId = "saved-offline", BleDeviceName = "Saved node", AutoConnect = false,
        }, CancellationToken);
        var root = services.GetRequiredService<MainWindowViewModel>();
        try
        {
            await root.LoadAsync(CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken);
            Assert.Equal(0, dispatcher.Calls);
            Assert.Equal(saved.Id, root.Profiles.SavedProfile!.Id);
            Assert.Equal(saved.BleDeviceId, root.Profiles.BleDevices!.SelectedDevice!.DeviceId);
            Assert.False(root.Profiles.IsDirty);
            Assert.False(root.Profiles.BleDevices.IsScanning);
            Assert.Equal(ConnectionSupervisorState.Offline, services.GetRequiredService<IConnectionSupervisor>().Snapshot.State);
        }
        finally
        {
            dispatcher.Ready = true;
            await root.StopAsync();
        }
    }

    private sealed class BootstrapDispatcher : IUiDispatcher
    {
        public bool Ready { get; set; }
        public int Calls { get; private set; }
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (!Ready) throw new InvalidOperationException("UI loop has not started.");
            cancellationToken.ThrowIfCancellationRequested();
            action(); return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task BootstrapCreatesBleClientsWithoutConnectingOrChangingSavedProfile()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await using var storage = await LocalStorage.OpenAsync(paths, CancellationToken);
        await using var services = AppBootstrap.CreateServiceProvider(paths, storage, null, uiDispatcher: new ImmediateUiDispatcher());
        var profile = await services.GetRequiredService<IConnectionProfileManager>().SaveAndSelectAsync(new()
        {
            Name = "Saved offline BLE", Transport = ConnectionTransportKind.Ble,
            BleDeviceId = "offline-device", BleDeviceName = "Display hint",
        }, CancellationToken);
        Assert.NotNull(services.GetRequiredService<IBleDeviceAccessDispatcher>());
        Assert.NotNull(services.GetRequiredService<ICompanionTransportFactory>());
        var factory = services.GetRequiredService<IMeshCoreClientFactory>();
#if WINDOWS
        await using var first = factory.Create(profile);
        await using var second = factory.Create(profile);
        Assert.NotSame(first, second);
        Assert.False(first.IsConnected); Assert.False(second.IsConnected);
#else
        Assert.Equal(MeshCoreBleFailureReason.UnsupportedPlatform,
            Assert.Throws<MeshCoreBleTransportException>(() => factory.Create(profile)).Reason);
#endif
        Assert.Equal(profile, await storage.ConnectionProfiles.GetAsync(profile.Id, CancellationToken));
        Assert.Equal(ConnectionSupervisorState.Offline, services.GetRequiredService<IConnectionSupervisor>().Snapshot.State);
    }

    [Theory]
    [InlineData(DesktopThemePreference.System)]
    [InlineData(DesktopThemePreference.Dark)]
    [InlineData(DesktopThemePreference.Light)]
    public async Task SettingsThemeUsesExistingPreferencesAndSurvivesReload(DesktopThemePreference theme)
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await using var storage = await LocalStorage.OpenAsync(paths, CancellationToken);
        await using var services = AppBootstrap.CreateServiceProvider(paths, storage, null, uiDispatcher: new ImmediateUiDispatcher());
        var vm = services.GetRequiredService<MainWindowViewModel>();
        await vm.LoadAsync(CancellationToken);
        Assert.Equal(new[] { "Авто", "Тёмная", "Светлая" }, vm.ThemeOptions.Select(option => option.Title));

        vm.SelectedTheme = vm.ThemeOptions.Single(option => option.Value == theme);
        Assert.Equal(theme, vm.SelectedTheme.Value);
        var expectedVariant = theme switch
        {
            DesktopThemePreference.Dark => Avalonia.Styling.ThemeVariant.Dark,
            DesktopThemePreference.Light => Avalonia.Styling.ThemeVariant.Light,
            _ => Avalonia.Styling.ThemeVariant.Default,
        };
        Assert.Equal(expectedVariant, App.ToThemeVariant(vm.SelectedTheme.Value));
        await services.GetRequiredService<DesktopPreferences>().FlushAsync(CancellationToken);
        var restored = new DesktopPreferences(storage.Settings);
        await restored.LoadAsync(CancellationToken);
        Assert.Equal(theme, restored.Snapshot.Theme);
        Assert.Equal(ConnectionSupervisorState.Offline, services.GetRequiredService<IConnectionSupervisor>().Snapshot.State);
        await vm.StopAsync();
    }

    [Fact]
    public async Task BootstrapProvidesStorageViewModelAndLogging()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await using var storage = await LocalStorage.OpenAsync(paths, CancellationToken);
        await using var services = AppBootstrap.CreateServiceProvider(paths, storage, null, uiDispatcher: new ImmediateUiDispatcher());

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
