using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MeshCoreMessenger.Desktop.Platform;

internal static class WindowsForegroundActivation
{
    public static void GrantToServer(NamedPipeClientStream pipe, int expectedProcessId)
    {
        if (!OperatingSystem.IsWindows()) return;
        // Verify the connected process rather than trusting a PID from a stale descriptor.
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId) || processId != expectedProcessId)
            throw new IOException("The activation pipe belongs to a different process.");
        // OS policy can deny the grant; showing the existing window must still be attempted.
        _ = AllowSetForegroundWindow(processId);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
