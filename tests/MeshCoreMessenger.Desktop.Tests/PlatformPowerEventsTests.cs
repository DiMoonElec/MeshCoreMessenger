using MeshCoreMessenger.Desktop.Platform;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class PlatformPowerEventsTests
{
    [Fact]
    public void CurrentPlatformAdapterStartsAndStops()
    {
        var events = DesktopPlatformPowerEvents.Create();

        Assert.IsAssignableFrom<IDisposable>(events);
        ((IDisposable)events).Dispose();
    }

    [Fact]
    public void MapsWindowsPowerBroadcastMessages()
    {
        Assert.Equal(
            PlatformPowerTransition.Suspending,
            WindowsPlatformPowerEvents.TranslateMessage(
                WindowsPlatformPowerEvents.PowerBroadcastMessage,
                WindowsPlatformPowerEvents.SuspendNotification));
        Assert.Equal(
            PlatformPowerTransition.Resumed,
            WindowsPlatformPowerEvents.TranslateMessage(
                WindowsPlatformPowerEvents.PowerBroadcastMessage,
                WindowsPlatformPowerEvents.ResumeSuspendNotification));
        Assert.Equal(
            PlatformPowerTransition.Resumed,
            WindowsPlatformPowerEvents.TranslateMessage(
                WindowsPlatformPowerEvents.PowerBroadcastMessage,
                WindowsPlatformPowerEvents.ResumeAutomaticNotification));
        Assert.Equal(
            PlatformPowerTransition.None,
            WindowsPlatformPowerEvents.TranslateMessage(
                0x000f,
                WindowsPlatformPowerEvents.SuspendNotification));
        Assert.Equal(
            PlatformPowerTransition.None,
            WindowsPlatformPowerEvents.TranslateMessage(
                WindowsPlatformPowerEvents.PowerBroadcastMessage,
                0x000a));
    }

    [Fact]
    public void MapsMacOsPowerMessages()
    {
        Assert.Equal(
            PlatformPowerTransition.Suspending,
            MacOsPlatformPowerEvents.TranslateMessage(MacOsPlatformPowerEvents.SystemWillSleepMessage));
        Assert.Equal(
            PlatformPowerTransition.Resumed,
            MacOsPlatformPowerEvents.TranslateMessage(MacOsPlatformPowerEvents.SystemHasPoweredOnMessage));
        Assert.Equal(
            PlatformPowerTransition.None,
            MacOsPlatformPowerEvents.TranslateMessage(MacOsPlatformPowerEvents.CanSystemSleepMessage));
        Assert.Equal(PlatformPowerTransition.None, MacOsPlatformPowerEvents.TranslateMessage(0xe0000320));
    }
}
