using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop.Presentation;

internal static class MessageCopy
{
    public static Task CopyAsync(HistoryMessageListItem message, Func<string, Task> writeText) =>
        writeText(message.CopyText);
}
