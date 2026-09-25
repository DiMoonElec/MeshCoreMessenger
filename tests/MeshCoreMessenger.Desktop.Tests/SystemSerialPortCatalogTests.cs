using MeshCoreMessenger.Desktop.Platform;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class SystemSerialPortCatalogTests
{
    [Fact]
    public async Task NormalizesSortsAndDeduplicatesPortNamesWithoutOpeningThem()
    {
        var calls = 0;
        var catalog = new SystemSerialPortCatalog(() =>
        {
            calls++;
            return [" COM9 ", "/dev/cu.usbserial-2", "COM9", "", "/dev/cu.usbserial-1"];
        });

        var ports = await catalog.GetPortNamesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["/dev/cu.usbserial-1", "/dev/cu.usbserial-2", "COM9"], ports);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PreCancelledEnumerationDoesNotCallOperatingSystemProvider()
    {
        var calls = 0;
        var catalog = new SystemSerialPortCatalog(() =>
        {
            calls++;
            return [];
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => catalog.GetPortNamesAsync(cancellation.Token));
        Assert.Equal(0, calls);
    }
}
