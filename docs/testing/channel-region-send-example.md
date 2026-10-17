# Пример одной отправки в канал с явным регионом

`samples/MeshCoreSharp.Console/ChannelRegionSendExample.cs` — отдельный opt-in пример.
Не вызывается общим runner, обычным self-test, startup или discovery. Физическая
отправка разрешена только по явному запросу пользователя; сообщение не повторяется
автоматически при timeout, отмене, неизвестном исходе или ошибке cleanup.

```powershell
dotnet run --project samples/MeshCoreSharp.Console -c Release --no-restore -- --ble-channel-region-send '<DeviceInformation.Id CatCore>' '#test' 'ru-ros' 'ИИ-агент: тест регионов' 'artifacts/channel-send-new.json' '<полный публичный ключ CatCore>'
```

Последний аргумент необязателен; при его наличии проверяется полный32-byte ключ
подключённой ноды до SEND. JSON-файл должен быть новым. Пример читает существующие
каналы, находит точное имя, отказывает при одинаковых именах с разными ключами и
повторно читает выбранный slot перед отправкой. Канал не создаётся и не перенастраивается;
его secret не выводится. Сообщения/backlog не выгружаются (`AutoReceiveMessages=false`).

Центральный вызов публичного API:

```csharp
var region = RegionDefinition.Public("ru-ros");
var scope = FloodScopeSelection.ExplicitRegion(region.Key);
var result = await client.SendChannelTextAsync(
    channel.Index, text, timestamp, scope, cancellationToken);
```

Библиотека сериализует temporary SET→SEND→RESET; пример не кодирует протокол вручную.
Лимит проверяется через `GetTextBudget(Channel, scope)` в UTF-8 байтах. После принятия
сохраняется `ScopeCleanupError`, затем повторно читается default. Успешный SEND —
локальный OK Companion, **не ACK доставки в канал**. Если cleanup/readback неудачен,
Accepted не превращается в Failed и SEND не повторяется.

В консоль и UTF-8 JSON выводятся node identity, channel/slot, public region name,
точный текст, timestamp, byte budget, Accepted, Cleanup, DefaultUnchanged и Error.
Статусы: NotSent, Rejected, Unknown, Accepted, AcceptedCleanupFailed,
AcceptedVerificationFailed. Результат можно прочитать агенту после окончания процесса:

```powershell
Get-Content -Raw -Encoding utf8 artifacts/channel-send-new.json
```

## Физическая проверка17.10.2026

14:23:27–14:23:34 MSK. CatCore `RnD CatCore🐈`, проверен полный ключ
`0C0CDFC96E503FFCBF31ECA1DB2E0D2CC6A72DEFDBBFEE25F4E122036EE71AD2`.
Вызов был прямо разрешён пользователем для этого сообщения/канала/региона.

| Параметр | Результат |
| --- | --- |
| Канал | `#test`, slot1 |
| Регион | `ru-ros`, ExplicitRegion |
| Текст | `ИИ-агент: тест регионов` |
| UTF-8 | 42bytes / budget143bytes |
| Wire timestamp | 1792236213 |
| SEND | Accepted, один вызов |
| Temporary scope cleanup | ResetConfirmed |
| Default после отправки | Не изменился |
| Ошибка | Нет |
| Доставка получателю | Пользователь17.10.2026 подтвердил получение на другой ноде с регионом `ru-ros` |

Подтверждение пользователя: «подтверждаю получение с регионом на другой ноде».
Это ручное подтверждение принимающей стороны для указанной отправки. Канальный API
по-прежнему не предоставляет delivery ACK; поле `RemoteDeliveryConfirmed=false`
в исходном JSON отражает данные API на момент отправки и не переписывается.

Локальные ignored artifacts:
`artifacts/channel-region-catcore-2026-10-17.json` и `.log`.
Повторять сообщение для регенерации отчёта не требуется.

## Проверки без радио

```powershell
dotnet run --project samples/MeshCoreSharp.Console -c Release --no-restore -- --channel-region-self-test
```

Пять сценариев: explicit wire key и точный Unicode-текст; SET rejected без SEND;
SEND rejected; принятый SEND с failed RESET без повторной передачи; отсутствующий
канал без SEND. Все прошли. Эта проверка также opt-in и не подключена к общему runner.
GUI не менялся; это ограниченная аппаратная проверка, не завершение всей REGION33.
