using System.Reflection;
using System.Runtime.Versioning;
using MeshCoreSharp;

internal static class BuildTargetTests
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Consumer resolves the matching Companion library target", MatchingLibraryTarget),
#if WINDOWS
        ("Windows Bluetooth WinRT APIs are available without connecting", WindowsBluetoothApi),
#endif
    ];

    private static Task MatchingLibraryTarget()
    {
        var library = typeof(MeshCoreClient).Assembly;
        var consumerTarget = typeof(BuildTargetTests).Assembly.GetCustomAttribute<TargetFrameworkAttribute>();
        var libraryTarget = library.GetCustomAttribute<TargetFrameworkAttribute>();
        if (consumerTarget?.FrameworkName != libraryTarget?.FrameworkName)
            throw new InvalidOperationException("Consumer and library framework targets differ.");

        var libraryPlatform = library.GetCustomAttribute<TargetPlatformAttribute>()?.PlatformName;
#if WINDOWS
        if (libraryPlatform != "Windows10.0.19041.0")
            throw new InvalidOperationException($"Expected Windows library, got {libraryPlatform ?? "portable"}.");
#else
        if (libraryPlatform is not null || library.GetReferencedAssemblies().Any(reference =>
                reference.Name is "WinRT.Runtime" or "Microsoft.Windows.SDK.NET" ||
                reference.Name?.StartsWith("Microsoft.WindowsAppSDK", StringComparison.Ordinal) == true))
            throw new InvalidOperationException("Portable library contains a Windows dependency.");
#endif
        return Task.CompletedTask;
    }

#if WINDOWS
    private static Task WindowsBluetoothApi()
    {
        // Only query API metadata: do not scan, pair, or open a device.
        if (!Windows.Foundation.Metadata.ApiInformation.IsTypePresent(
                typeof(Windows.Devices.Bluetooth.BluetoothLEDevice).FullName!) ||
            !Windows.Foundation.Metadata.ApiInformation.IsTypePresent(
                typeof(Windows.Devices.Bluetooth.GenericAttributeProfile.GattSession).FullName!))
            throw new InvalidOperationException("Required Windows Bluetooth APIs are unavailable.");
        return Task.CompletedTask;
    }
#endif
}
