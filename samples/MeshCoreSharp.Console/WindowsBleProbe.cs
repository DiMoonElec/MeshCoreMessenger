#if WINDOWS
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Transport.Ble;

internal static class WindowsBleProbe
{
    internal static async Task<int> RunAsync(BleProbeArguments args)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            await using var dispatcher = new BleProbeDispatcher();
            await dispatcher.Ready;
            await using var native = new BleMeshCoreTransport(new()
            {
                DeviceId = args.DeviceId, DeviceAccessDispatcher = dispatcher,
                OpenTimeout = TimeSpan.FromSeconds(30),
                MaximumFrameSize = args.MaximumFrameSize,
            });
            Console.WriteLine("BLE8 read-only probe: APP_START, DEVICE_QUERY, GET_DEVICE_TIME, GET_CONTACTS, GET_CHANNEL only.");
            Console.WriteLine($"Logical frame ceiling={args.MaximumFrameSize} bytes; outgoing writes use the current ATT payload budget.");
            if (args.MaximumFrameSize < MeshCoreSharp.Protocol.ProtocolLimits.CurrentFirmwareMaxFrameSize)
                Console.WriteLine("The explicit diagnostic frame ceiling also limits RX; omit --frame-limit for the stock firmware probe.");
            await BleReadOnlyProbe.RunAsync(new(native), args.Cycles,
                () => native.MaxPduSize, () => native.CleanupCompletion, cancellation.Token);
            Console.WriteLine(args.MaximumFrameSize == MeshCoreSharp.Protocol.ProtocolLimits.CurrentFirmwareMaxFrameSize
                ? "BLE PROBE PASSED" : "BLE LIMITED-FRAME DIAGNOSTIC PASSED; full-size firmware compatibility remains unverified.");
            return 0;
        }
        catch (MeshCoreBleTransportException error)
        {
            Console.Error.WriteLine($"BLE probe: {error.Reason}; HRESULT=0x{error.HResult:X8}; status={error.NativeStatus}; protocol={error.ProtocolError}: {error.Message}");
            return 1;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("BLE probe cancelled or overall deadline expired.");
            return 130;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"BLE probe failed: {error}");
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}

// A real STA host with a Win32 message loop for FromIdAsync/OS consent, not a ThreadPool dispatcher.
// Only the console sample uses this: Desktop already has its own UI dispatcher.
internal sealed class BleProbeDispatcher : IBleDeviceAccessDispatcher, IAsyncDisposable
{
    private const uint WorkMessage = 0x8001;
    private const uint QuitMessage = 0x0012;
    private readonly object _sync = new();
    private readonly ConcurrentQueue<(Action Action, CancellationToken Token, TaskCompletionSource Completion)> _work = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private uint _threadId;
    private bool _closed;
    internal Task Ready => _ready.Task;

    internal BleProbeDispatcher()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = "BLE8 device access STA" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_ready.Task.IsCompletedSuccessfully) throw new InvalidOperationException("Await dispatcher.Ready first.");
            _work.Enqueue((action, cancellationToken, completion));
            if (!PostThreadMessageW(_threadId, WorkMessage, 0, 0))
                completion.TrySetException(new Win32Exception(Marshal.GetLastWin32Error()));
        }
        return completion.Task;
    }

    private void Pump()
    {
        nint window = 0;
        try
        {
            _threadId = GetCurrentThreadId();
            window = CreateWindowExW(0, "STATIC", "MeshCore BLE8 device access", 0,
                0, 0, 0, 0, new nint(-3), 0, 0, 0); // Invisible message-only window.
            if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _ready.TrySetResult();
            int result;
            while ((result = GetMessageW(out var message, 0, 0, 0)) > 0)
            {
                if (message.Message == WorkMessage)
                {
                    while (_work.TryDequeue(out var item))
                    {
                        if (item.Completion.Task.IsCompleted) continue;
                        try { item.Token.ThrowIfCancellationRequested(); item.Action(); item.Completion.TrySetResult(); }
                        catch (OperationCanceledException) { item.Completion.TrySetCanceled(item.Token); }
                        catch (Exception error) { item.Completion.TrySetException(error); }
                    }
                }
                else { TranslateMessage(ref message); DispatchMessageW(ref message); }
            }
            if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception error) { _ready.TrySetException(error); }
        finally
        {
            lock (_sync) _closed = true;
            while (_work.TryDequeue(out var item)) item.Completion.TrySetException(new ObjectDisposedException(nameof(BleProbeDispatcher)));
            if (window != 0) DestroyWindow(window);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            PostThreadMessageW(_threadId, QuitMessage, 0, 0);
        }
        if (!await Task.Run(() => _thread.Join(TimeSpan.FromSeconds(2))))
            throw new TimeoutException("BLE probe STA host did not stop.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window; public uint Message; public nuint WParam; public nint LParam;
        public uint Time; public int X; public int Y; public uint Private;
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessageW(out NativeMessage message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref NativeMessage message);
}
#endif
