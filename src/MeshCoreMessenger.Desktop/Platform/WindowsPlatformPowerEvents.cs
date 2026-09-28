using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MeshCoreMessenger.Desktop.Platform;

internal sealed class WindowsPlatformPowerEvents : PlatformPowerEventsBase, IDisposable
{
    internal const uint PowerBroadcastMessage = 0x0218;
    internal const nuint SuspendNotification = 0x0004;
    internal const nuint ResumeSuspendNotification = 0x0007;
    internal const nuint ResumeAutomaticNotification = 0x0012;
    private const uint CloseMessage = 0x0010;
    private const uint DestroyMessage = 0x0002;
    private static readonly IntPtr MessageOnlyWindow = new(-3);

    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WindowProcedure _windowProcedure;
    private readonly string _className = $"MeshCoreMessenger.Power.{Guid.NewGuid():N}";
    private IntPtr _windowHandle;
    private int _disposed;

    public WindowsPlatformPowerEvents()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows power events require Windows.");
        }

        _windowProcedure = HandleWindowMessage;
        _thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "MeshCoreMessenger Windows power events",
        };
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
    }

    internal static PlatformPowerTransition TranslateMessage(uint message, nuint parameter)
    {
        if (message != PowerBroadcastMessage)
        {
            return PlatformPowerTransition.None;
        }

        return parameter switch
        {
            SuspendNotification => PlatformPowerTransition.Suspending,
            ResumeSuspendNotification or ResumeAutomaticNotification => PlatformPowerTransition.Resumed,
            _ => PlatformPowerTransition.None,
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var window = Interlocked.CompareExchange(ref _windowHandle, IntPtr.Zero, IntPtr.Zero);
        if (window != IntPtr.Zero)
        {
            NativeMethods.PostMessageW(window, CloseMessage, UIntPtr.Zero, IntPtr.Zero);
        }
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join();
        }
    }

    private void RunMessageLoop()
    {
        var instance = NativeMethods.GetModuleHandleW(null);
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Instance = instance,
            ClassName = _className,
            WindowProcedure = _windowProcedure,
        };

        try
        {
            if (NativeMethods.RegisterClassExW(ref windowClass) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the power-event window class.");
            }

            var window = NativeMethods.CreateWindowExW(
                0,
                _className,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                MessageOnlyWindow,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);
            if (window == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the power-event window.");
            }

            Interlocked.Exchange(ref _windowHandle, window);
            _ready.TrySetResult();
            while (NativeMethods.GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessageW(ref message);
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
        }
        finally
        {
            Interlocked.Exchange(ref _windowHandle, IntPtr.Zero);
            NativeMethods.UnregisterClassW(_className, instance);
        }
    }

    private IntPtr HandleWindowMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam)
    {
        var transition = TranslateMessage(message, (nuint)wParam.ToUInt64());
        if (transition != PlatformPowerTransition.None)
        {
            Raise(transition);
            return new IntPtr(1);
        }

        if (message == CloseMessage)
        {
            NativeMethods.DestroyWindow(window);
            return IntPtr.Zero;
        }
        if (message == DestroyMessage)
        {
            NativeMethods.PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProcW(window, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public WindowProcedure? WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr BackgroundBrush;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandleW(string? moduleName);

        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
        internal static extern ushort RegisterClassExW(ref WindowClass windowClass);

        [DllImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterClassW(string className, IntPtr instance);

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateWindowExW(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr parameter);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        internal static extern IntPtr DefWindowProcW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessageW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
        internal static extern int GetMessageW(out WindowMessage message, IntPtr window, uint minimum, uint maximum);

        [DllImport("user32.dll", EntryPoint = "TranslateMessage")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TranslateMessage(ref WindowMessage message);

        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
        internal static extern IntPtr DispatchMessageW(ref WindowMessage message);

        [DllImport("user32.dll", EntryPoint = "PostQuitMessage")]
        internal static extern void PostQuitMessage(int exitCode);
    }
}
