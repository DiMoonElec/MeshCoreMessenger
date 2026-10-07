using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreMessenger.Desktop.Preferences;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreMessenger.Desktop.Views;

namespace MeshCoreMessenger.Desktop.Bootstrap;

public static class AppBootstrap
{
    public static ServiceProvider CreateServiceProvider(IAppPaths paths, LocalStorage storage) =>
        CreateServiceProvider(paths, storage, null);

    internal static ServiceProvider CreateServiceProvider(
        IAppPaths paths, LocalStorage storage, DesktopActivationCoordinator? activation)
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
        services.AddSingleton(storage.IncomingMessages);
        services.AddSingleton(storage.OutgoingMessages);
        services.AddSingleton(storage.History);
        services.AddSingleton(storage.ContactDeliveries);
        services.AddSingleton(storage.HistoryClear);
        services.AddSingleton(storage.ReadStates);
        services.AddSingleton(storage.Drafts);
        services.AddSingleton(storage.ConversationDirectory);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISearchDelay, SystemSearchDelay>();
        services.AddSingleton<IDraftDelay, SystemDraftDelay>();
        services.AddSingleton<DesktopPreferences>();
        services.AddSingleton<IOutgoingTextProcessor, PassthroughOutgoingTextProcessor>();
        services.AddSingleton<ISendReadinessReader, SendReadinessReader>();
        services.AddSingleton<IDurableDesktopPreferences>(provider =>
            provider.GetRequiredService<DesktopPreferences>());
        services.AddSingleton<IConnectionProfileManager, ConnectionProfileManager>();
        services.AddSingleton<SessionCompletionTracker>();
        services.AddSingleton<IDurableSessionCompletion>(provider =>
            provider.GetRequiredService<SessionCompletionTracker>());
        services.AddSingleton<ConversationReadStateTracker>();
        services.AddSingleton<IDurableReadStateWrites>(provider =>
            provider.GetRequiredService<ConversationReadStateTracker>());
        services.AddSingleton<DraftWriteTracker>();
        services.AddSingleton<IDraftBuffer>(provider =>
            provider.GetRequiredService<DraftWriteTracker>());
        services.AddSingleton<IDurableDraftWrites>(provider =>
            provider.GetRequiredService<DraftWriteTracker>());
        services.AddSingleton<IMeshCoreClientFactory, MeshCoreClientFactory>();
        services.AddSingleton<ICompanionSessionFactory, CompanionSessionFactory>();
        services.AddSingleton<DirectoryService>();
        services.AddSingleton<MessageIngestor>();
        services.AddSingleton<IDurableMessageIngress>(provider =>
            provider.GetRequiredService<MessageIngestor>());
        services.AddSingleton<IDurableOutgoingWrites, OutgoingAttemptWriteTracker>();
        services.AddSingleton<ConversationOperationGuard>();
        services.AddSingleton<IHistoryClearService, HistoryClearService>();
        services.AddSingleton<IContactRouteService, ContactRouteService>();
        services.AddSingleton<PrivateDeliveryCoordinator>();
        services.AddSingleton<IMessageService, MessageService>();
        services.AddSingleton<SessionCommandGateway>();
        services.AddSingleton<ISessionCommandGateway>(provider => provider.GetRequiredService<SessionCommandGateway>());
        services.AddSingleton<IConnectionAttemptFactory, ConnectionAttemptFactory>();
        services.AddSingleton<IConnectionFailureClassifier, ConnectionFailureClassifier>();
        services.AddSingleton<IReconnectDelay, SystemReconnectDelay>();
        services.AddSingleton<IReconnectJitter, RandomReconnectJitter>();
        services.AddSingleton<IPlatformPowerEvents>(_ => DesktopPlatformPowerEvents.Create());
        services.AddSingleton<IConnectionSupervisor, ConnectionSupervisor>();
        services.AddSingleton<DesktopConnectionLifecycle>();
        services.AddSingleton<IDesktopConnectionLifecycle>(provider =>
            provider.GetRequiredService<DesktopConnectionLifecycle>());
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        if (activation is not null) services.AddSingleton(activation);
        else services.AddSingleton<DesktopActivationCoordinator>();
        services.AddSingleton<IMessageCommitNotifications, MessageCommitNotifications>();
        services.AddSingleton<ISerialPortCatalog, SystemSerialPortCatalog>();
        services.AddSingleton<ConnectionProfilesViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<IDesktopUiLifetime>(provider =>
            provider.GetRequiredService<MainWindowViewModel>());
        services.AddSingleton<DesktopShutdownCoordinator>();
        services.AddSingleton<IDesktopShutdownCoordinator>(provider =>
            provider.GetRequiredService<DesktopShutdownCoordinator>());
        services.AddTransient(provider => new MainWindow(
            provider.GetRequiredService<IDesktopShutdownCoordinator>())
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
