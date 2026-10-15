namespace MeshCoreSharp.Transport.Ble;

/// <summary>Runs device-access initiation on the host UI thread, where Windows may request consent.</summary>
public interface IBleDeviceAccessDispatcher
{
    /// <summary>Completes only after the action finishes, or if it was cancelled before execution.</summary>
    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}
