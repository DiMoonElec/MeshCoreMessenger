using Avalonia.Threading;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.Bootstrap;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Notifications;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreMessenger.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    public static class NativeShutdownThreadAudit
    {
        public static async Task RunAsync()
        {
            await using var workspace = await Workspace.CreateAsync();
            await workspace.Root.StopAsync();
            var paths = DesktopAppPaths.CreateForDirectory(Path.GetDirectoryName(workspace.StoragePath())!);
            await using var services = AppBootstrap.CreateServiceProvider(paths, workspace.Storage);
            var root = services.GetRequiredService<MainWindowViewModel>();
            await root.LoadAsync();
            var notifications = services.GetRequiredService<MessageNotificationCoordinator>();
            notifications.Start();
            var window = services.GetRequiredService<MainWindow>();
            try
            {
                window.Show(); await SettleAsync(window);
                root.RetryPreferencesSaveCommand.CanExecuteChanged += (_, _) => Dispatcher.UIThread.VerifyAccess();
                root.Chats.Public.Navigation.History.PropertyChanged += (_, _) => Dispatcher.UIThread.VerifyAccess();
                root.Chats.Private.Navigation.History.PropertyChanged += (_, _) => Dispatcher.UIThread.VerifyAccess();
                var shutdown = services.GetRequiredService<DesktopShutdownCoordinator>();
                await shutdown.ShutdownAsync();
                if (!shutdown.IsCompleted || root.ErrorMessage is not null)
                    throw new InvalidOperationException("Native shutdown failed to complete durable barriers.");
                if (services.GetRequiredService<IConnectionSupervisor>().Snapshot.State != ConnectionSupervisorState.Offline)
                    throw new InvalidOperationException("Offline native shutdown changed connection state.");
                window.Close(); await SettleAsync(window);
                Console.WriteLine("Native shutdown: real DI/notification workers/Avalonia bindings; command and viewport notifications on UI thread; durable barrier completed; zero connection startup.");
            }
            finally { window.Close(); }
        }
    }
}
