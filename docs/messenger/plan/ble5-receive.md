# BLE5 — Подписка и непрерывный приём кадров

[Stage BLE](stage-ble.md) · [Оглавление](../../MESSENGER_PLAN.md)

**Статус: реализован 16.10.2026.**

**Цель:** Превратить notifications в ReceiveAsync без блокировки callbacks.

**Зависимости:** BLE4. Проверить результаты зависимостей перед началом.
Общие инварианты и решения — в [Stage BLE](stage-ble.md).

**Файлы/область:** `Transport/Ble/`, `tests/MeshCoreSharp.Tests`.
Новые имена/папки — предлагаемое место реализации; проверить актуальные исходники.

## Шаги

1. Создать очередь приёма и зарегистрировать ValueChanged до записи CCCD Notify. Сохранить кадры, пришедшие во время включения подписки.
2. Только после успешного CCCD и готовности GATT переводить транспорт в Ready/IsConnected=true.
3. В callback копировать bytes в принадлежащую сессии память и быстро помещать кадр в очередь. ReceiveAsync отдаёт логические кадры в порядке получения, без stream decoder.
4. Очередь ограничить; при переполнении завершать сессию явной ошибкой, а не молча терять сообщения. Не блокировать callback ожиданием свободного места.
5. Пустые/чрезмерные транспортные значения диагностировать; неизвестный тип нормального кадра передавать общему decoder. Не склеивать notifications по догадкам.

**Не входит:** Изменение packet parser, команда SYNC_NEXT_MESSAGE, обработка UI/БД.

## Проверки и завершение

**Тесты/проверки:** Первый notification внутри CCCD operation; push до первого consumer и между ответами; порядок/копирование памяти; неизвестный код; overflow; cancel ReceiveAsync; notification старой generation.

**Готово:** Непрерывный RX готов до первой команды, пользовательские callbacks не исполняются на GATT callback.

**Ручная проверка:** Не требуется до BLE8.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; фактические результаты записаны ниже.
- [x] Статус подэтапа, checklist Stage BLE и current-state обновлены.

## Результат выполнения

16.10.2026: добавлен internal `BleReceiveSession` поверх подготовленной BLE4 generation.
Очередь bounded, callbacks зарегистрированы до CCCD; Ready выставляется только после
успешной подписки и повторной проверки link/MTU. Notifications копируются и передаются
в порядке входа без stream framing. Channel запрещает synchronous continuations:
consumer не запускается внутри GATT callback. Неизвестный код сохраняется как есть.
Пустое/слишком большое значение терминально диагностируется через InvalidFrame;
переполнение — через ReceiveQueueOverflow. Отмена отдельного ReceiveAsync не закрывает
соединение. Закрытая generation игнорирует сохранённые старые callbacks.

В Windows connection реализованы `IBleGattNotifications`, ValueChanged и CCCD Notify
через WriteClientCharacteristicConfigurationDescriptorWithResultAsync с проверкой
native status/protocol error. Native I/O не бросается на полпути при отмене caller wait:
освобождение services/device ждёт окончания операции, cleanup наблюдается отдельно.
На GATT callback native cleanup не выполняется. Полный lifecycle, bounded best-effort
CCCD disable и повторный Connect одного transport остаются BLE7.
Public transport и SendAsync добавляются в BLE6; production DI не менялся.

| Проверка | Результат |
| --- | --- |
| `dotnet build MeshCoreSharp.sln -c Release --no-restore` | 0 warnings/errors |
| `dotnet run --project tests/MeshCoreSharp.Tests -c Release --no-build` | **143/143** |
| `dotnet restore MeshCoreSharp.sln -p:McmWindowsDesktop=false --locked-mode` | Успешно |
| `dotnet build MeshCoreSharp.sln -c Release -p:McmWindowsDesktop=false --no-restore` | 0 warnings/errors |
| `dotnet run --project tests/MeshCoreSharp.Tests -c Release -p:McmWindowsDesktop=false --no-build` | **141/141** |

Восемь новых cases: первый notification до CCCD completion, порядок/копирование/unknown,
overflow, invalid lengths, отмена reader, отказ/отмена/поздний успех подписки,
disconnect/MTU drop и старая generation. Реальный GATT и native macOS не проверялись;
ручная проверка не требуется до BLE8. Core/DB/runtime и полные Core/Desktop suites
не затрагивались; известное ограничение Desktop suite сохраняется.

Источник Windows API: [Microsoft GATT client](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client).
