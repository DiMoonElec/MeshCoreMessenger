# BLE7 — Разрыв, отмена и освобождение BLE-сессии

[Stage BLE](stage-ble.md) · [Оглавление](../../MESSENGER_PLAN.md)

**Статус: реализован 16.10.2026.**

**Цель:** Закрывать транспорт предсказуемо при любой точке сбоя.

**Зависимости:** BLE5, BLE6. Проверить результаты зависимостей перед началом.
Общие инварианты и решения — в [Stage BLE](stage-ble.md).

**Файлы/область:** `Transport/Ble/`, библиотечные lifecycle tests.
Новые имена/папки — предлагаемое место реализации; проверить актуальные исходники.

## Шаги

1. Свести normal disconnect, open failure, write failure, overflow и внешний disconnect к одному идемпотентному cleanup.
2. Запретить новые writes, завершить receive channel, отписать callbacks и освобождать service/session/device в корректном порядке. CCCD disable — best effort с ограниченным ожиданием.
3. Не ждать бесконечно неотменяемую Windows operation. Её completion наблюдается; поздно возвращённый ресурс освобождается владельцем старой generation. Не допускать Dispose ресурса под использующим его I/O без обоснованного безопасного пути.
4. Не включать вечное MaintainConnection. При разрыве терминально завершать эту попытку; повторное подключение создаёт новую session, решение принимает приложение.
5. DisconnectAsync/DisposeAsync не ждут пользовательские callbacks. Для повторного Connect того же transport определить и проверить новую generation.

**Не входит:** Изменение supervisor backoff, восстановления истории или outgoing replay.

## Проверки и завершение

**Тесты/проверки:** Disconnect во время каждого шага open/subscribe/write; повторный dispose; поздние notifications/results; unplug/off; queue completion пробуждает reader; отсутствие unobserved exceptions и двойного dispose.

**Готово:** Все созданные ресурсы имеют путь освобождения, старая попытка не влияет на новую; RX и pending command получают terminal outcome.

**Ручная проверка:** Настоящие сбои Windows проверяются в BLE8/BLE17.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; фактические результаты записаны ниже.
- [x] Статус подэтапа, checklist Stage BLE и current-state обновлены.

## Результат выполнения

16.10.2026: завершён lifecycle поверх BLE5/BLE6. `BleFrameTransport` теперь хранит
отдельную generation для каждой явной попытки Connect: собственные stop source,
receiver, open completion и physical cleanup completion. Повторный Connect при Ready
идемпотентен, параллельное открытие отклоняется. После Disconnect/ошибки новый Connect
создаёт другую generation, даже если старая ещё освобождает ресурсы. Dispose терминален
для объекта, повторный Dispose не освобождает ресурсы дважды. Автоматического reconnect,
replay или MaintainConnection=true не добавлено.

Disconnect, open/subscribe/write failure, RX overflow и внешний link loss сходятся
в идемпотентный retire/Stop. Новые writes запрещены, receive channel завершается,
pending write/command получает terminal outcome, callbacks отсоединяются в cleanup
worker. Старый reader привязан к старой очереди: новая попытка его не перенаправляет.
Поздний native result/callback не меняет state следующей generation.

`BleReceiveSession` резервирует владение I/O до вызова native метода, включая случай
синхронного disconnect callback из этого метода. Cleanup ждёт actual completion всех
запущенных subscribe/write операций, затем при доступном link пробует CCCD None,
после чего закрывает attempt и освобождает service/session/device существующим Windows
backend. Отмена wait не считается завершением native I/O. При disable timeout его
token отменяется, но native Task продолжает наблюдаться владельцем; Dispose ресурса
не выполняется под этой операцией. Ошибка disable не мешает последующему освобождению.

Добавлен ShutdownTimeout (по умолчанию 2 секунды; конечный положительный CTS-compatible
interval): он ограничивает caller wait Disconnect/Dispose и best-effort CCCD disable.
При истечении caller wait logical сессия уже закрыта, physical cleanup может оставаться
pending. Public `BleMeshCoreTransport.CleanupCompletion` возвращает snapshot завершения
cleanup всех созданных к этому моменту generations; при активной сессии он pending до
её закрытия. Late cleanup failures наблюдаются internally и доступны caller через этот
Task (AggregateException). Успешно завершённые старые задачи убираются при следующем
Connect; незавершённые/ошибочные сохраняются. Ошибки старой generation не закрывают
новую. Отмена Disconnect token прерывает лишь ожидание caller, не освобождение ресурсов.
BLE4 opener также ограничивает ожидание Dispose при неуспешной подготовке, сохраняя
её actual cleanup в CleanupCompletion и признак BleCleanupPending в primary error.
Если поздний native open завершился ошибкой с BleCleanupError от частично созданных
ресурсов, этот cleanup failure также передаётся в public CleanupCompletion.

Physical cleanup может ждать неотменяемую Windows operation дольше ShutdownTimeout;
это намеренное сохранение владельца ресурса, а не обещание аппаратного disconnect за
2 секунды. Если native Task никогда не завершится, его ресурсы остаются у старого
владельца, а logical API уже завершено. Ограничение Windows проверяется на ноде в BLE8.

| Проверка | Результат |
| --- | --- |
| `dotnet restore MeshCoreSharp.sln --locked-mode` | Успешно |
| `dotnet build MeshCoreSharp.sln -c Release --no-restore` | 0 warnings/errors |
| `dotnet run --project tests/MeshCoreSharp.Tests -c Release --no-build` | **165/165** |
| `dotnet restore MeshCoreSharp.sln -p:McmWindowsDesktop=false --locked-mode` | Успешно |
| `dotnet build MeshCoreSharp.sln -c Release -p:McmWindowsDesktop=false --no-restore` | 0 warnings/errors |
| `dotnet run --project tests/MeshCoreSharp.Tests -c Release -p:McmWindowsDesktop=false --no-build` | **163/163** |
| Windows locked restore после portable проверки | Успешно |

После добавления последних regression cases отдельно пересобраны test projects обоих
targets; 0 warnings/errors. Тринадцать новых cases покрывают pending native open,
MTU wait, CCCD subscribe, active/queued write, bounded CCCD disable и late fault,
failed open/reconnect/repeated dispose, radio-off/unplug через fake, cleanup failure,
caller cancellation при disconnect, медленный Dispose после ошибки подготовки,
синхронный callback внутри write start, late partial cleanup failure и MeshCoreClient Dispose с заблокированным
пользовательским event handler и pending command. Старые BLE/TCP/Serial cases зелёные.
При явном Disconnect во время Connect его результат может быть отменой либо terminal
transport error в зависимости от того, выиграла ли отмена или закрытие receive session;
обе ветви завершают generation и не допускают Ready после разрыва.

Core/runtime/DB, DI и supervisor не менялись. Полные Core/Desktop suites не запускались;
известное ограничение Desktop suite сохраняется. Реальные Windows radio-off/unplug,
GATT и native macOS не проверялись. Следующий подэтап — **BLE8**, в этом запросе не начат.

Источник ограничений Windows: [Microsoft GATT client](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client).
