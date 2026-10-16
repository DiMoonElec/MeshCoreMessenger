using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Exceptions;

namespace MeshCoreMessenger.Core.Application;

public enum ConnectionFailureDisposition
{
    Transient,
    NeedsAttention,
}

public interface IConnectionFailureClassifier
{
    ConnectionFailureDisposition Classify(Exception exception);
}

public sealed class ConnectionFailureClassifier : IConnectionFailureClassifier
{
    public static string? Describe(Exception? exception)
    {
        if (exception is null) return null;
        return Unwrap(exception) is MeshCoreBleTransportException ble ? ble.Reason switch
        {
            MeshCoreBleFailureReason.UnsupportedPlatform => "Подключение BLE доступно только на Windows.",
            MeshCoreBleFailureReason.PairingRequired => "Сопрягите ноду в настройках Bluetooth Windows и повторите подключение.",
            MeshCoreBleFailureReason.AccessDenied => "Windows запретила доступ к BLE-устройству. Проверьте разрешения.",
            MeshCoreBleFailureReason.AdapterUnavailable => "Адаптер Bluetooth недоступен. Проверьте его подключение.",
            MeshCoreBleFailureReason.RadioOff => "Включите Bluetooth и повторите подключение.",
            MeshCoreBleFailureReason.DeviceUnreachable => "BLE-нода недоступна. Проверьте питание, расстояние и подключение другого клиента.",
            MeshCoreBleFailureReason.MissingService => "Устройство не предоставляет BLE-сервис MeshCore Companion.",
            MeshCoreBleFailureReason.MissingCharacteristic or MeshCoreBleFailureReason.UnsupportedCharacteristics => "BLE-характеристики устройства несовместимы с MeshCore Companion.",
            MeshCoreBleFailureReason.InsufficientMtu => "Не удалось согласовать допустимый размер BLE-пакета.",
            MeshCoreBleFailureReason.ReceiveQueueOverflow => "Переполнена очередь BLE-приёма. Часть данных могла быть потеряна.",
            MeshCoreBleFailureReason.IncompleteNotification => "Целостность входящего BLE-сообщения не подтверждена. Часть сообщения могла быть потеряна.",
            MeshCoreBleFailureReason.InvalidFrame => "Получен недопустимый BLE-кадр.",
            MeshCoreBleFailureReason.WriteOutcomeUnknown => "Результат BLE-записи неизвестен. Переподключение само по себе не повторяет отправку.",
            _ => "Ошибка обмена по Bluetooth LE. Повторите подключение.",
        } : exception.Message;
    }

    public ConnectionFailureDisposition Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var current = Unwrap(exception);
        return current switch
        {
            ConnectionAttemptPersistenceException => ConnectionFailureDisposition.NeedsAttention,
            OutgoingPersistenceException => ConnectionFailureDisposition.NeedsAttention,
            ReceiveIngestException => ConnectionFailureDisposition.NeedsAttention,
            SessionCompletionPersistenceException => ConnectionFailureDisposition.NeedsAttention,
            DatabaseStorageException => ConnectionFailureDisposition.NeedsAttention,
            SqliteException => ConnectionFailureDisposition.NeedsAttention,
            NotSupportedException => ConnectionFailureDisposition.NeedsAttention,
            ArgumentException => ConnectionFailureDisposition.NeedsAttention,
            MeshCoreProtocolException => ConnectionFailureDisposition.NeedsAttention,
            MeshCoreCommandException => ConnectionFailureDisposition.NeedsAttention,
            ReceiveDrainTimeoutException => ConnectionFailureDisposition.Transient,
            MeshCoreBleTransportException ble => ble.Reason switch
            {
                MeshCoreBleFailureReason.AdapterUnavailable or MeshCoreBleFailureReason.RadioOff or
                MeshCoreBleFailureReason.DeviceUnreachable or MeshCoreBleFailureReason.TransportFailure or
                MeshCoreBleFailureReason.WriteOutcomeUnknown => ConnectionFailureDisposition.Transient,
                _ => ConnectionFailureDisposition.NeedsAttention,
            },
            MeshCoreTransportException => ConnectionFailureDisposition.Transient,
            MeshCoreTimeoutException => ConnectionFailureDisposition.Transient,
            SocketException => ConnectionFailureDisposition.Transient,
            TimeoutException => ConnectionFailureDisposition.Transient,
            IOException => ConnectionFailureDisposition.Transient,
            _ => ConnectionFailureDisposition.NeedsAttention,
        };
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
        {
            exception = aggregate.InnerExceptions[0];
        }

        return exception;
    }
}
