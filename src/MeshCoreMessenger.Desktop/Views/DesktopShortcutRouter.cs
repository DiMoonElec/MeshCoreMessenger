using Avalonia.Input;

namespace MeshCoreMessenger.Desktop.Views;

internal enum DesktopShortcutAction
{
    None,
    FocusDirectorySearch,
    FocusHistorySearch,
    CloseSettings,
    ClearDirectorySearch,
    ClearHistorySearch,
    NavigateBack,
}

internal sealed record DesktopShortcutContext(
    Key Key,
    KeyModifiers Modifiers,
    bool IsTextInputFocused,
    bool HasConversation,
    bool IsDetailVisible,
    bool IsSettingsOpen,
    bool HasDirectorySearch,
    bool HasHistorySearch,
    bool CanNavigateBack);

internal static class DesktopShortcutRouter
{
    public static DesktopShortcutAction Route(DesktopShortcutContext context)
    {
        var primary = (context.Modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (primary && context.Key == Key.F)
        {
            return context.HasConversation && context.IsDetailVisible
                ? DesktopShortcutAction.FocusHistorySearch
                : DesktopShortcutAction.FocusDirectorySearch;
        }

        // Text editors, including an active IME composition, own Escape/Alt+Left. Search boxes
        // are handled by the view before constructing this context.
        if (context.IsTextInputFocused)
        {
            return DesktopShortcutAction.None;
        }

        if (context.Key == Key.Escape)
        {
            if (context.IsSettingsOpen)
            {
                return DesktopShortcutAction.CloseSettings;
            }

            if (context.HasHistorySearch)
            {
                return DesktopShortcutAction.ClearHistorySearch;
            }

            if (context.HasDirectorySearch)
            {
                return DesktopShortcutAction.ClearDirectorySearch;
            }

            return context.CanNavigateBack
                ? DesktopShortcutAction.NavigateBack
                : DesktopShortcutAction.None;
        }

        return context.Key == Key.Left &&
            (context.Modifiers & KeyModifiers.Alt) != 0 &&
            context.CanNavigateBack
                ? DesktopShortcutAction.NavigateBack
                : DesktopShortcutAction.None;
    }
}
