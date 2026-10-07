using System.Globalization;
using System.Text;
using MeshCoreSharp.Models;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Preferences;
using Microsoft.Extensions.Logging;

namespace MeshCoreMessenger.Desktop.Notifications;

internal sealed record MessageNotificationGroup(IncomingMessageCommitEvent Latest, long FirstSequence, int Count, bool IsOverflow = false);
internal sealed record MessageNotificationRequest(IReadOnlyList<MessageNotificationGroup> Groups,
    bool IsSummary)
    : NotificationRequest("", "", null, "messages:" + (IsSummary ? "summary:" : "conversation:") +
        (Groups.Count > 0 ? Groups[0].Latest.Message.NodeId.ToString("N") + ":" +
        (IsSummary ? Groups[0].Latest.SessionId : Groups[0].Latest.Message.ConversationId).ToString("N") : "overflow"));

internal interface IMessageNotificationVisibility
{
    Task<bool> IsVisibleAsync(MessageNotificationGroup group, CancellationToken cancellationToken);
}

/// <summary>Rechecks settings, exact persisted content, and actual viewport immediately before delivery.</summary>
internal sealed class MessageNotificationPolicy(DesktopPreferences preferences, IMessageDetailsReader reader,
    IMessageNotificationVisibility visibility, ILogger<MessageNotificationPolicy> logger) : INotificationRequestPolicy
{
    public async Task<NotificationRequest?> PrepareAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        if (request is not MessageNotificationRequest messages) return request;
        var settings = preferences.Snapshot;
        var privateCount = 0;
        var channelCount = 0;
        CommittedMessageDetails? latest = null;
        foreach (var group in messages.Groups)
        {
            var category = group.Latest.Category;
            if (!(category == IncomingMessageCategory.Private ? settings.NotifyPrivateMessages : settings.NotifyChannelMessages)) continue;
            try
            {
                var id = group.Latest.Message;
                var details = await reader.GetAsync(id.NodeId, id.ConversationId, id.MessageId, cancellationToken).ConfigureAwait(false);
                if (details is null || details.Direction != MessageDirection.Incoming) continue;
                var actualCategory = details.ConversationKind is ConversationKind.Contact or ConversationKind.UnknownContact
                    ? IncomingMessageCategory.Private : IncomingMessageCategory.Channel;
                if (actualCategory != category) continue;
                // One representative cannot prove that all conversations in an overflow accumulator were viewed.
                if (!group.IsOverflow && await visibility.IsVisibleAsync(group, cancellationToken).ConfigureAwait(false)) continue;
                latest = details;
                if (category == IncomingMessageCategory.Private) privateCount += group.Count; else channelCount += group.Count;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { logger.LogWarning("Notification projection failed ({ErrorType}).", error.GetType().Name); }
        }
        settings = preferences.Snapshot;
        if (!settings.NotifyPrivateMessages) privateCount = 0;
        if (!settings.NotifyChannelMessages) channelCount = 0;
        var count = privateCount + channelCount;
        if (count == 0) return null;
        if (messages.IsSummary || latest is null)
            return new("MeshCoreMessenger", $"Новые сообщения: {count} (личные: {privateCount}, каналы: {channelCount})",
                new OpenApplicationTarget(), messages.CoalescingKey);
        var title = latest.ConversationKind switch
        {
            ConversationKind.UnknownContact => "Неизвестный контакт",
            ConversationKind.UnknownChannel => "Канал не определён",
            ConversationKind.Contact => SafeText(latest.ConversationName, 80, "Контакт без имени"),
            _ => SafeText(latest.ConversationName, 80, "Канал без названия"),
        };
        var preview = latest.MessageKind == StoredMessageKind.Binary ? "Двоичное сообщение"
            : latest.TextType is (int)MessageTextType.Plain or (int)MessageTextType.SignedPlain ? SafeText(latest.Text, 160, "Новое сообщение") : "Новое сообщение";
        return new(title, count > 1 ? $"Новых сообщений: {count}. {preview}" : preview,
            new MessageNotificationTarget(latest.NodeId, latest.ConversationId, latest.MessageId), messages.CoalescingKey);
    }

    internal static string SafeText(string? text, int limit, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var clean = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Control || category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                clean.Append(' ');
            else if (category != UnicodeCategory.Format || rune.Value == 0x200D) clean.Append(rune.ToString());
        }
        var normalized = string.Join(' ', clean.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0) return fallback;
        var elements = StringInfo.ParseCombiningCharacters(normalized);
        return elements.Length > limit ? normalized[..elements[limit]] + "…" : normalized;
    }
}
