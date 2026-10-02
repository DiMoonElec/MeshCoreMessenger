using Avalonia.Input;
using MeshCoreMessenger.Desktop.Views;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class DesktopShortcutRouterTests
{
    [Theory]
    [InlineData(KeyModifiers.Control)]
    [InlineData(KeyModifiers.Meta)]
    public void PrimaryFindRoutesByVisibleContext(KeyModifiers modifiers)
    {
        Assert.Equal(
            DesktopShortcutAction.FocusHistorySearch,
            Route(Key.F, modifiers, hasConversation: true, detail: true));
        Assert.Equal(
            DesktopShortcutAction.FocusDirectorySearch,
            Route(Key.F, modifiers, hasConversation: false));
    }

    [Fact]
    public void EscapeClosesOverlayThenSearchThenNarrowDetail()
    {
        Assert.Equal(DesktopShortcutAction.CloseSettings, Route(Key.Escape, settings: true));
        Assert.Equal(DesktopShortcutAction.ClearHistorySearch, Route(Key.Escape, historySearch: true));
        Assert.Equal(DesktopShortcutAction.ClearDirectorySearch, Route(Key.Escape, directorySearch: true));
        Assert.Equal(DesktopShortcutAction.NavigateBack, Route(Key.Escape, canBack: true));
    }

    [Fact]
    public void TextEditorAndImeOwnEscapeAndNavigationKeysAndNoRouteCanSend()
    {
        Assert.Equal(DesktopShortcutAction.None, Route(Key.Escape, textInput: true, canBack: true));
        Assert.Equal(
            DesktopShortcutAction.None,
            Route(Key.Left, KeyModifiers.Alt, textInput: true, canBack: true));
        Assert.DoesNotContain(Enum.GetNames<DesktopShortcutAction>(), name =>
            name.Contains("Send", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AltLeftNavigatesBackOnlyWhenAvailable()
    {
        Assert.Equal(
            DesktopShortcutAction.NavigateBack,
            Route(Key.Left, KeyModifiers.Alt, canBack: true));
        Assert.Equal(DesktopShortcutAction.None, Route(Key.Left, KeyModifiers.Alt));
    }

    private static DesktopShortcutAction Route(
        Key key,
        KeyModifiers modifiers = KeyModifiers.None,
        bool textInput = false,
        bool hasConversation = false,
        bool detail = false,
        bool settings = false,
        bool directorySearch = false,
        bool historySearch = false,
        bool canBack = false) =>
        DesktopShortcutRouter.Route(new DesktopShortcutContext(
            key,
            modifiers,
            textInput,
            hasConversation,
            detail,
            settings,
            directorySearch,
            historySearch,
            canBack));
}
