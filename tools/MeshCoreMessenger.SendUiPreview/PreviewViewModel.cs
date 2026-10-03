using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.Presentation;
using MeshCoreMessenger.Desktop.ViewModels;

namespace MeshCoreMessenger.SendUiPreview;

public sealed class PreviewViewModel : ObservableObject
{
    private static readonly DateTimeOffset Time = new(2026, 10, 3, 14, 36, 0, TimeSpan.Zero);
    private string _senderName = "Наша нода 🐈";
    private TextOptimizationLevel _optimizationLevel;
    public PreviewViewModel()
    {
        PublicComposer = new ComposerViewModel { Text = "Канал: @[Очень длинное имя ноды 🐈] привет 👋",
            Context = new(true), Readiness = SendReadiness.Offline, PreviewExplanation = "Черновик не сохранён" };
        PrivateComposer = new ComposerViewModel { Text = "Личный независимый черновик 👋",
            Readiness = SendReadiness.Offline, PreviewExplanation = "Черновик не сохранён" };
        PublicMessages = CreateMessages(channel: true);
        PrivateMessages = CreateMessages(channel: false);
        ClearExplanation = new RelayCommand(() => SetExplanations(SendReadiness.Ready, string.Empty));
        OfflineExplanation = new RelayCommand(() => SetExplanations(SendReadiness.Offline, "Черновик не сохранён"));
        ErrorExplanation = new RelayCommand(() => SetExplanations(SendReadiness.AmbiguousPrefix, "Не удалось сохранить черновик"));
    }
    public ComposerViewModel PublicComposer { get; }
    public ComposerViewModel PrivateComposer { get; }
    public IReadOnlyList<HistoryMessageListItem> PublicMessages { get; }
    public IReadOnlyList<HistoryMessageListItem> PrivateMessages { get; }
    public RelayCommand ClearExplanation { get; }
    public RelayCommand OfflineExplanation { get; }
    public RelayCommand ErrorExplanation { get; }

    public string SenderName
    {
        get => _senderName;
        set
        {
            if (SetProperty(ref _senderName, value))
                PublicComposer.Context = new(true, PublicComposer.Readiness == SendReadiness.Ready ? value : null);
        }
    }
    public IReadOnlyList<TextOptimizationLevel> OptimizationLevels { get; } = Enum.GetValues<TextOptimizationLevel>();
    public TextOptimizationLevel OptimizationLevel
    {
        get => _optimizationLevel;
        set
        {
            if (!SetProperty(ref _optimizationLevel, value)) return;
            PublicComposer.Options = PrivateComposer.Options = new(value);
        }
    }

    private void SetExplanations(SendReadiness readiness, string explanation)
    {
        foreach (var composer in new[] { PublicComposer, PrivateComposer })
        {
            composer.Readiness = readiness;
            composer.PreviewExplanation = explanation;
        }
        PublicComposer.Context = new(true, readiness == SendReadiness.Ready ? SenderName : null);
    }

    private static IReadOnlyList<HistoryMessageListItem> CreateMessages(bool channel)
    {
        var messages = new List<HistoryMessageListItem>();
        var conversation = Guid.NewGuid();
        messages.Add(new HistoryMessageListItem(new HistoryMessage(Guid.NewGuid(), 1, conversation,
            MessageDirection.Incoming, StoredMessageKind.Text,
            "Очень длинное имя собеседника: @[Наша нода 🐈] сообщение принято 👋\nВторая строка — исходный текст для копирования.", Time))
            { Presentation = new(Hops: 5, Details: "Демонстрационные метаданные входящего сообщения.") });
        var states = channel
            ? new[] { MessageSendDisplayState.Prepared, MessageSendDisplayState.Sending, MessageSendDisplayState.AcceptedByNode,
                MessageSendDisplayState.Failed, MessageSendDisplayState.Unknown }
            : Enum.GetValues<MessageSendDisplayState>();
        foreach (var state in states)
        {
            var item = new HistoryMessageListItem(new HistoryMessage(Guid.NewGuid(), messages.Count + 1,
                conversation, MessageDirection.Outgoing, StoredMessageKind.Text,
                $"Демонстрация: {state}. Кириллица, emoji 🐈 и @[Наша нода 🐈].", Time));
            var retryable = state is MessageSendDisplayState.Failed or MessageSendDisplayState.Unknown;
            item.Presentation = new(state, state == MessageSendDisplayState.Sending ? 3 : 1,
                state == MessageSendDisplayState.Sending ? 5 : null,
                HeardRelays: channel ? (retryable ? 0 : state == MessageSendDisplayState.AcceptedByNode ? 2 : null) : null,
                Details: "ДЕМО: данные не сохранены и не переданы. 3/5 — только макет; автоматических повторов нет.",
                RetryVisible: retryable || state == MessageSendDisplayState.Unconfirmed,
                RetryEnabled: retryable,
                RetryCommand: new RelayCommand(() => item.Presentation = item.Presentation with
                {
                    State = MessageSendDisplayState.Sending,
                    AttemptNumber = (item.Presentation.AttemptNumber ?? 0) + 1,
                    RetryVisible = false, RetryEnabled = false,
                    Details = "ДЕМО: обновлены метаданные того же пузырька. Запросы client/store отсутствуют.",
                }));
            messages.Add(item);
        }
        return messages;
    }
}
