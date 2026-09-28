using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MeshCoreMessenger.Desktop.Platform;

internal sealed class MacOsPlatformPowerEvents : PlatformPowerEventsBase, IDisposable
{
    internal const uint CanSystemSleepMessage = 0xe0000270;
    internal const uint SystemWillSleepMessage = 0xe0000280;
    internal const uint SystemHasPoweredOnMessage = 0xe0000300;
    private const uint Utf8Encoding = 0x08000100;

    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SystemPowerCallback _callback;
    private IntPtr _runLoop;
    private uint _rootPowerPort;
    private int _disposed;

    public MacOsPlatformPowerEvents()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("macOS power events require macOS.");
        }

        _callback = HandlePowerMessage;
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "MeshCoreMessenger macOS power events",
        };
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
    }

    internal static PlatformPowerTransition TranslateMessage(uint message) => message switch
    {
        SystemWillSleepMessage => PlatformPowerTransition.Suspending,
        SystemHasPoweredOnMessage => PlatformPowerTransition.Resumed,
        _ => PlatformPowerTransition.None,
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var runLoop = Interlocked.CompareExchange(ref _runLoop, IntPtr.Zero, IntPtr.Zero);
        if (runLoop != IntPtr.Zero)
        {
            NativeMethods.CFRunLoopStop(runLoop);
        }
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join();
        }
    }

    private void RunLoop()
    {
        IntPtr notificationPort = IntPtr.Zero;
        IntPtr source = IntPtr.Zero;
        IntPtr mode = IntPtr.Zero;
        uint notifier = 0;
        try
        {
            _rootPowerPort = NativeMethods.IORegisterForSystemPower(
                IntPtr.Zero,
                out notificationPort,
                _callback,
                out notifier);
            if (_rootPowerPort == 0 || notificationPort == IntPtr.Zero)
            {
                throw new Win32Exception("Could not register for macOS system power notifications.");
            }

            source = NativeMethods.IONotificationPortGetRunLoopSource(notificationPort);
            mode = NativeMethods.CFStringCreateWithCString(
                IntPtr.Zero,
                "kCFRunLoopDefaultMode",
                Utf8Encoding);
            var runLoop = NativeMethods.CFRunLoopGetCurrent();
            if (source == IntPtr.Zero || mode == IntPtr.Zero || runLoop == IntPtr.Zero)
            {
                throw new InvalidOperationException("Could not initialize the macOS power-event run loop.");
            }

            Interlocked.Exchange(ref _runLoop, runLoop);
            NativeMethods.CFRunLoopAddSource(runLoop, source, mode);
            _ready.TrySetResult();
            NativeMethods.CFRunLoopRun();
            NativeMethods.CFRunLoopRemoveSource(runLoop, source, mode);
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
        }
        finally
        {
            Interlocked.Exchange(ref _runLoop, IntPtr.Zero);
            if (notifier != 0)
            {
                NativeMethods.IODeregisterForSystemPower(ref notifier);
            }
            if (notificationPort != IntPtr.Zero)
            {
                NativeMethods.IONotificationPortDestroy(notificationPort);
            }
            if (_rootPowerPort != 0)
            {
                NativeMethods.IOServiceClose(_rootPowerPort);
                _rootPowerPort = 0;
            }
            if (mode != IntPtr.Zero)
            {
                NativeMethods.CFRelease(mode);
            }
        }
    }

    private void HandlePowerMessage(IntPtr reference, uint service, uint message, IntPtr argument)
    {
        if (message is CanSystemSleepMessage or SystemWillSleepMessage)
        {
            NativeMethods.IOAllowPowerChange(_rootPowerPort, argument);
        }

        var transition = TranslateMessage(message);
        if (transition != PlatformPowerTransition.None)
        {
            Raise(transition);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SystemPowerCallback(IntPtr reference, uint service, uint message, IntPtr argument);

    private static class NativeMethods
    {
        private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(IOKit)]
        internal static extern uint IORegisterForSystemPower(
            IntPtr reference,
            out IntPtr notificationPort,
            SystemPowerCallback callback,
            out uint notifier);

        [DllImport(IOKit)]
        internal static extern int IODeregisterForSystemPower(ref uint notifier);

        [DllImport(IOKit)]
        internal static extern int IOAllowPowerChange(uint rootPowerPort, IntPtr notificationId);

        [DllImport(IOKit)]
        internal static extern IntPtr IONotificationPortGetRunLoopSource(IntPtr notificationPort);

        [DllImport(IOKit)]
        internal static extern void IONotificationPortDestroy(IntPtr notificationPort);

        [DllImport(IOKit)]
        internal static extern int IOServiceClose(uint service);

        [DllImport(CoreFoundation)]
        internal static extern IntPtr CFRunLoopGetCurrent();

        [DllImport(CoreFoundation)]
        internal static extern void CFRunLoopRun();

        [DllImport(CoreFoundation)]
        internal static extern void CFRunLoopStop(IntPtr runLoop);

        [DllImport(CoreFoundation)]
        internal static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);

        [DllImport(CoreFoundation)]
        internal static extern void CFRunLoopRemoveSource(IntPtr runLoop, IntPtr source, IntPtr mode);

        [DllImport(CoreFoundation)]
        internal static extern IntPtr CFStringCreateWithCString(
            IntPtr allocator,
            [MarshalAs(UnmanagedType.LPUTF8Str)]
            string value,
            uint encoding);

        [DllImport(CoreFoundation)]
        internal static extern void CFRelease(IntPtr value);
    }
}
