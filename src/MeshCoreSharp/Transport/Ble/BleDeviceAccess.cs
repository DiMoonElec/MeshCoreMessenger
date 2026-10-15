namespace MeshCoreSharp.Transport.Ble;

internal static class BleDeviceAccess
{
    // Do not abandon a device-producing operation: cancellation of its wait could lose an IClosable result.
    // The opener bounds the caller's wait and observes this entire resource-owning task after timeout.
    internal static async Task<T?> OpenAsync<T>(IBleDeviceAccessDispatcher dispatcher,
        Func<Task<T?>> createDevice, CancellationToken cancellationToken) where T : class, IDisposable
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<T?>? opening = null;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                opening = createDevice();
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (opening is not null)
            {
                // A host dispatcher may fail after running the action. It must not orphan its device.
                var orphan = await opening.ConfigureAwait(false);
                orphan?.Dispose();
            }
            throw;
        }
        var device = await (opening ?? throw new InvalidOperationException("Device access action was not executed."))
            .ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            device?.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return device;
    }
}
