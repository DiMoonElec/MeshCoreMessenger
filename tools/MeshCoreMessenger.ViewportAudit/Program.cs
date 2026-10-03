using Avalonia;
using Avalonia.Threading;
using MeshCoreMessenger.Desktop;
using MeshCoreMessenger.Desktop.Tests;

AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
Exception? failure = null;
var completed = false;
Dispatcher.UIThread.Post(async () =>
{
    try { await UiWorkspaceIntegrationTests.NativeAudit.RunAsync(!args.Contains("--measure-only")); }
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
