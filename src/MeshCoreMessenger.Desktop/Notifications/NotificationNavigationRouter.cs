using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.Desktop.Notifications;

/// <summary>Runs on UI context. Opens local history only; never owns connection or device configuration.</summary>
internal sealed class NotificationNavigationRouter(MainWindowViewModel root, IConversationDirectoryReader directory, ILocalHistoryReader history)
{
    public async Task OpenAsync(NotificationTarget target, CancellationToken token)
    {
        if (target is not MessageNotificationTarget message) return;
        if (root.Modal.IsOpen || root.ViewedNode?.Id != message.NodeId)
        { root.ReportNotificationFallback("Уведомление относится к другой ноде или сейчас открыт диалог. Окно приложения открыто."); return; }
        var position = await history.GetMessagePositionAsync(message.NodeId, message.ConversationId, message.MessageId, token);
        if (position is null) { root.ReportNotificationFallback("Сообщение из уведомления уже недоступно."); return; }
        ConversationDirectoryEntry? entry = null;
        foreach (var section in Enum.GetValues<ConversationDirectorySection>())
        {
            ConversationDirectoryCursor? cursor = null;
            for (var pageNumber = 0; pageNumber < 100; pageNumber++)
            {
                var page = await directory.GetPageAsync(message.NodeId, section, cursor, 200, token);
                entry = page.Items.FirstOrDefault(item => item.ConversationId == message.ConversationId);
                if (entry is not null || page.NextCursor is null) break;
                cursor = page.NextCursor;
            }
            if (entry is not null) break;
        }
        if (entry is null || root.ViewedNode?.Id != message.NodeId || root.Modal.IsOpen)
        { root.ReportNotificationFallback("Переписка из уведомления уже недоступна в текущем окне."); return; }
        root.Shell.SelectSection(entry.Kind is ConversationKind.Channel or ConversationKind.UnknownChannel ? ShellSection.PublicChats : ShellSection.PrivateChats);
        await root.SelectConversationAsync(new ConversationListItem(entry), token);
        if (root.ViewedNode?.Id != message.NodeId || root.SelectedConversation?.Id != message.ConversationId) return;
        if (!await root.Navigation.History.JumpToSearchResultAsync(new HistorySearchResultListItem(
            new HistoryMessageSearchResult(position, MessageDirection.Incoming, "", DateTimeOffset.MinValue)), token))
            root.ReportNotificationFallback("Сообщение из уведомления уже недоступно в текущем окне.");
    }
}
