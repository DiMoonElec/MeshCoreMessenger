using Avalonia;
using Avalonia.Threading;
using MeshCoreMessenger.Desktop;
using MeshCoreMessenger.Desktop.Tests;

try
{
    if (!MacDisplayStartupProbe.Check()) return 1;
    if (args.Contains("--startup-check-only")) return 0;

    AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    Exception? failure = null;
    var completed = false;
    Dispatcher.UIThread.Post(async () =>
    {
        try
        {
            if (args.Contains("--native-notifications-only")) await UiWorkspaceIntegrationTests.NativeNotificationIntegrationAudit.RunAsync();
            else if (args.Contains("--placement-only")) await UiWorkspaceIntegrationTests.NativeWindowPlacementAudit.RunAsync();
            else if (args.Contains("--shutdown-only")) await UiWorkspaceIntegrationTests.NativeShutdownThreadAudit.RunAsync();
            else if (args.Contains("--notifications-only")) await UiWorkspaceIntegrationTests.NativeNotificationPolicyAudit.RunAsync();
            else if (args.Contains("--tray-only")) await UiWorkspaceIntegrationTests.NativeTrayLifecycleAudit.RunAsync();
            else if (args.Contains("--settings-only")) await UiWorkspaceIntegrationTests.NativeSettingsPreferencesAudit.RunAsync();
            else if (args.Contains("--instance-activation-only")) await UiWorkspaceIntegrationTests.NativeInstanceActivationAudit.RunAsync();
            else if (args.Contains("--repeat-channel-only")) await UiWorkspaceIntegrationTests.NativeChannelRepeatAudit.RunAsync();
            else if (args.Contains("--modal-only")) await UiWorkspaceIntegrationTests.NativeModalAudit.RunAsync();
            else if (args.Contains("--route-reset-only")) await UiWorkspaceIntegrationTests.NativeContactRouteAudit.RunAsync();
            else if (args.Contains("--history-clear-only")) await UiWorkspaceIntegrationTests.NativeHistoryClearAudit.RunAsync();
            else if (args.Contains("--private-send-only")) await UiWorkspaceIntegrationTests.NativePrivateSendAudit.RunAsync();
            else if (args.Contains("--switch-only")) await UiWorkspaceIntegrationTests.NativeSwitchAudit.RunAsync();
            else if (args.Contains("--send-only")) await UiWorkspaceIntegrationTests.NativeSendAudit.RunAsync();
            else await UiWorkspaceIntegrationTests.NativeAudit.RunAsync(!args.Contains("--measure-only"));
        }
        catch (Exception error) { failure = error; }
        finally { completed = true; lifetime.Cancel(); }
    });
    Dispatcher.UIThread.MainLoop(lifetime.Token);
    if (!completed) { Console.Error.WriteLine("Viewport audit timed out."); return 1; }
    if (failure is not null)
    {
        Console.Error.WriteLine(failure);
        return 1;
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("Viewport audit startup/execution failed:");
    Console.Error.WriteLine(error);
    return 1;
}
