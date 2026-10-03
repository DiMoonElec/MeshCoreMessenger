using System.Windows.Input;

namespace MeshCoreMessenger.Desktop.Presentation;

// UI vocabulary, not a second persistence/protocol state machine.
public enum MessageSendDisplayState { Prepared, Sending, AwaitingAck, Delivered, AcceptedByNode, Unconfirmed, Failed, Unknown }

public sealed record MessagePresentation(
    MessageSendDisplayState? State = null,
    int? AttemptNumber = null,
    int? AttemptLimit = null,
    int? HeardRelays = null,
    int? Hops = null,
    string? Details = null,
    bool RetryVisible = false,
    bool RetryEnabled = false,
    ICommand? RetryCommand = null);

public static class MessageMetadataFormatter
{
    public static string Format(string time, MessagePresentation presentation)
    {
        var parts = new List<string> { time };
        if (presentation.State is { } state)
        {
            var status = state switch
            {
                MessageSendDisplayState.Prepared => "Подготовлено",
                MessageSendDisplayState.Sending => "Отправка",
                MessageSendDisplayState.AwaitingAck => "Ожидается подтверждение",
                MessageSendDisplayState.Delivered => "Доставлено",
                MessageSendDisplayState.AcceptedByNode => "Принято нодой",
                MessageSendDisplayState.Unconfirmed => "Доставка не подтверждена",
                MessageSendDisplayState.Failed => "Ошибка",
                _ => "Результат неизвестен",
            };
            if (state == MessageSendDisplayState.Sending && presentation.AttemptNumber is { } attempt)
                status += presentation.AttemptLimit is { } limit
                    ? $" (попытка {attempt}/{limit})" : $" (попытка {attempt})";
            parts.Add(status);
        }
        if (presentation.Hops is { } hops) parts.Add($"Хопов: {hops}");
        if (presentation.HeardRelays is { } relays) parts.Add($"Ретрансляторов: {relays}");
        return string.Join(" • ", parts);
    }

    public static string Explanation(MessageSendDisplayState? state) => state switch
    {
        MessageSendDisplayState.Prepared => "Сообщение подготовлено. Вызов отправки ещё не начат.",
        MessageSendDisplayState.Sending => "Сообщение передаётся компаньону.",
        MessageSendDisplayState.AwaitingAck => "Личное сообщение принято нодой. Ожидается ACK.",
        MessageSendDisplayState.Delivered => "Получен ACK адресата. Это не означает прочтение человеком.",
        MessageSendDisplayState.AcceptedByNode => "Сообщение принято нодой. Подтверждение доставки адресатам не предусмотрено.",
        MessageSendDisplayState.Unconfirmed => "ACK не получен вовремя. Сообщение могло быть доставлено.",
        MessageSendDisplayState.Failed => "Произошёл отказ отправки. Подробности указаны ниже, если они известны.",
        MessageSendDisplayState.Unknown => "Достоверный результат отправки неизвестен. Сообщение могло быть доставлено.",
        _ => string.Empty,
    };
}
