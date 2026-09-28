namespace MeshCoreMessenger.Core.Application;

/// <summary>Reports operating-system suspend and resume transitions.</summary>
public interface IPlatformPowerEvents
{
    event EventHandler? Suspending;
    event EventHandler? Resumed;
}
