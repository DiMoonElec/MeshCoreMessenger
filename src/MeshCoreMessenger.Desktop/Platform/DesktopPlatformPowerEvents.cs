using MeshCoreMessenger.Core.Application;

namespace MeshCoreMessenger.Desktop.Platform;

public static class DesktopPlatformPowerEvents
{
    public static IPlatformPowerEvents Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsPlatformPowerEvents();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsPlatformPowerEvents();
        }

        return new UnsupportedPlatformPowerEvents();
    }
}

internal enum PlatformPowerTransition
{
    None,
    Suspending,
    Resumed,
}

internal abstract class PlatformPowerEventsBase : IPlatformPowerEvents
{
    public event EventHandler? Suspending;
    public event EventHandler? Resumed;

    protected void Raise(PlatformPowerTransition transition)
    {
        var handlers = transition switch
        {
            PlatformPowerTransition.Suspending => Suspending,
            PlatformPowerTransition.Resumed => Resumed,
            _ => null,
        };

        foreach (EventHandler handler in handlers?.GetInvocationList().Cast<EventHandler>() ?? [])
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // A lifecycle observer must not terminate the native notification thread.
            }
        }
    }
}

internal sealed class UnsupportedPlatformPowerEvents : PlatformPowerEventsBase, IDisposable
{
    public void Dispose()
    {
    }
}
