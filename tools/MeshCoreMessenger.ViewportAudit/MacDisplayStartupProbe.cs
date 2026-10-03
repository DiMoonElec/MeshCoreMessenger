using System.Runtime.InteropServices;

internal static class MacDisplayStartupProbe
{
    // Read-only preflight: do not start the link, wake the screen or change power settings.
    internal static bool Check()
    {
        if (!OperatingSystem.IsMacOS()) return true;
        var displays = new uint[32];
        var displayStatus = CGGetActiveDisplayList((uint)displays.Length, displays, out var count);
        var status = CVDisplayLinkCreateWithActiveCGDisplays(out var link);
        try
        {
            Console.Error.WriteLine($"macOS display preflight: main={CGMainDisplayID()}, active={count}, CoreGraphics={displayStatus}, CVDisplayLink={status}.");
            if (status == 0 && link != IntPtr.Zero) return true;
            Console.Error.WriteLine("Display render timer unavailable. Retry with the display awake and an unlocked desktop. This check does not determine whether the screen is locked.");
            return false;
        }
        finally
        {
            if (link != IntPtr.Zero) CVDisplayLinkRelease(link);
        }
    }

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern uint CGMainDisplayID();

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern int CGGetActiveDisplayList(uint maxDisplays, [Out] uint[] displays, out uint count);

    [DllImport("/System/Library/Frameworks/CoreVideo.framework/CoreVideo")]
    private static extern int CVDisplayLinkCreateWithActiveCGDisplays(out IntPtr link);

    [DllImport("/System/Library/Frameworks/CoreVideo.framework/CoreVideo")]
    private static extern void CVDisplayLinkRelease(IntPtr link);
}
