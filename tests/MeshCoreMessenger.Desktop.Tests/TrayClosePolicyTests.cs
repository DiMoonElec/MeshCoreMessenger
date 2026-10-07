using Avalonia.Controls;
using MeshCoreMessenger.Desktop.Preferences;
using MeshCoreMessenger.Desktop.Views;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class TrayClosePolicyTests
{
    [Theory]
    [InlineData(WindowCloseReason.WindowClosing, DesktopCloseBehavior.MinimizeToTray, true, true)]
    [InlineData(WindowCloseReason.WindowClosing, DesktopCloseBehavior.ExitApplication, true, false)]
    [InlineData(WindowCloseReason.WindowClosing, DesktopCloseBehavior.MinimizeToTray, false, false)]
    [InlineData(WindowCloseReason.ApplicationShutdown, DesktopCloseBehavior.MinimizeToTray, true, false)]
    [InlineData(WindowCloseReason.OSShutdown, DesktopCloseBehavior.MinimizeToTray, true, false)]
    [InlineData(WindowCloseReason.OwnerWindowClosing, DesktopCloseBehavior.MinimizeToTray, true, false)]
    [InlineData(WindowCloseReason.Undefined, DesktopCloseBehavior.MinimizeToTray, true, false)]
    public void OnlyUserCloseWithAvailableTrayCanHide(WindowCloseReason reason,
        DesktopCloseBehavior behavior, bool available, bool hides) =>
        Assert.Equal(hides, MainWindow.ShouldHideOnClose(reason, behavior, available));
}
