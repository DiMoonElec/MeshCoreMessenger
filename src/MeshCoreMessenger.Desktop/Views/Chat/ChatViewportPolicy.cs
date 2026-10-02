namespace MeshCoreMessenger.Desktop.Views.Chat;

internal readonly record struct RealizedMessageBounds(int Index, double Y, double Height);

internal static class ChatViewportPolicy
{
    public static int[] VisibleIndices(IEnumerable<RealizedMessageBounds> containers, double viewportHeight, int messageCount) =>
        containers.Where(item => item.Index >= 0 && item.Index < messageCount && item.Height > 0 &&
            viewportHeight > 0 && item.Y + item.Height > 0 && item.Y < viewportHeight)
            .Select(item => item.Index).Order().ToArray();

    public static bool CanReportRead(bool attached, bool visible, bool active) => attached && visible && active;
    public static bool CanApplyCallback(long queuedRevision, long currentRevision, bool attached, bool visible) =>
        queuedRevision == currentRevision && attached && visible;
}
