using System.Diagnostics;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreMessenger.Desktop.Preferences;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Desktop.Tests;

// Deliberately launches the normal executable before any Avalonia initialization in
// this process. In-process UI audits already have a dispatcher and miss bootstrap races.
public static class NativeBleStartupAudit
{
    public static async Task RunAsync(string executable)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows startup audit only.");
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("Build Desktop first.", executable);
        var data = Path.Combine(Path.GetTempPath(), "MeshCoreMessenger.BleStartup", Guid.NewGuid().ToString("N"));
        var paths = DesktopAppPaths.CreateForDirectory(data);
        Guid nodeId;
        try
        {
            await using (var storage = await LocalStorage.OpenAsync(paths))
            {
                var profiles = new ConnectionProfileManager(storage.ConnectionProfiles, storage.Settings, TimeProvider.System);
                var profile = await profiles.SaveAndSelectAsync(new()
                {
                    Name = "Offline BLE startup", Transport = ConnectionTransportKind.Ble,
                    BleDeviceId = "offline-startup-test", AutoConnect = false, Reconnect = false,
                });
                var now = DateTimeOffset.UtcNow;
                var node = await storage.Nodes.FindOrCreateAsync(new byte[32], "Offline history", now);
                nodeId = node.Id;
                var session = Guid.NewGuid();
                await storage.Sessions.CreateAsync(new(session, profile.Id, node.Id, now, null, null));
                await storage.Sessions.EndAsync(session, now, "Startup fixture");
                var directory = await storage.Directories.ApplySnapshotAsync(node.Id, session, [],
                    [new(0, "Startup channel", new byte[32], ChannelAccessKind.Unknown)], now);
                await storage.IncomingMessages.StoreAsync(new(Guid.NewGuid(), session, node.Id,
                    new ChannelMessage(0, 1, MessageTextType.Plain, now, "Сохранённая история 👋", 0),
                    now, directory.ActiveBindings.Single(), null));
                await storage.Settings.SetAsync(DesktopPreferences.CloseBehaviorSettingKey, DesktopCloseBehavior.ExitApplication.ToString());
            }
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                using var process = new Process
                {
                    StartInfo = new(executable)
                    {
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                    },
                };
                process.StartInfo.ArgumentList.Add("--data-dir");
                process.StartInfo.ArgumentList.Add(data);
                process.Start();
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    while (true)
                    {
                        process.Refresh();
                        if (process.HasExited) throw new InvalidOperationException($"Desktop exited before showing a window: {await errors}");
                        if (process.MainWindowHandle != IntPtr.Zero) break;
                        await Task.Delay(50, timeout.Token);
                    }
                    if (!process.CloseMainWindow()) throw new InvalidOperationException("Could not close test window.");
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    if (process.ExitCode != 0) throw new InvalidOperationException($"Desktop exit {process.ExitCode}: {await errors}");
                    Console.WriteLine($"Saved BLE startup {attempt}: real Desktop window shown and closed normally.");
                }
                finally
                {
                    if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                    await Task.WhenAll(output, errors);
                }
            }
            await using var reopened = await LocalStorage.OpenAsync(paths);
            if ((await reopened.ConnectionProfiles.GetAllAsync()).Count != 1 ||
                (await reopened.Nodes.GetAllAsync(10)).Count != 1)
                throw new InvalidOperationException("Startup changed the offline fixture.");
            var conversation = (await reopened.History.GetConversationsAsync(nodeId, 10)).Single();
            var message = (await reopened.History.GetMessagesAsync(nodeId, conversation.Id, null, 10)).Single();
            if (message.Text != "Сохранённая история 👋") throw new InvalidOperationException("History changed on restart.");
        }
        finally { Directory.Delete(data, true); }
    }
}
