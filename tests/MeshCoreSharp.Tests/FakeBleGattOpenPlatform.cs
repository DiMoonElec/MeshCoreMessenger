using System.Threading.Channels;
using MeshCoreSharp.Transport.Ble;

internal sealed class FakeBleGattOpenPlatform : IBleGattOpenPlatform
{
    internal sealed record Open(string DeviceId, ControlledBleOperation<IBleGattConnection?> Operation);
    internal Channel<Open> Opens { get; } = Channel.CreateUnbounded<Open>();

    public Task<IBleGattConnection?> OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        var operation = new ControlledBleOperation<IBleGattConnection?>(cancellationToken);
        Opens.Writer.TryWrite(new(deviceId, operation));
        return operation.Task;
    }
}

internal sealed class FakeBleDeviceAccessDispatcher : IBleDeviceAccessDispatcher
{
    internal sealed record Invocation(Action Action, TaskCompletionSource Completion, CancellationToken CancellationToken)
    {
        internal void Run()
        {
            try { Action(); Completion.SetResult(); }
            catch (Exception error) { Completion.SetException(error); }
        }
    }
    internal Channel<Invocation> Invocations { get; } = Channel.CreateUnbounded<Invocation>();
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Invocations.Writer.TryWrite(new(action, completion, cancellationToken));
        return completion.Task;
    }
}

internal sealed class FakeBleNativeDevice : IDisposable
{
    internal int DisposeCount;
    public void Dispose() => Interlocked.Increment(ref DisposeCount);
}
