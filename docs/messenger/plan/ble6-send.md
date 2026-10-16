# BLE6 — Передача целого Companion-кадра

[Stage BLE](stage-ble.md) · [Оглавление](../../MESSENGER_PLAN.md)

**Статус: реализован 16.10.2026.**

**Цель:** Реализовать SendAsync с определённым результатом записи.

**Зависимости:** BLE5. Проверить результаты зависимостей перед началом.
Общие инварианты и решения — в [Stage BLE](stage-ble.md).

**Файлы/область:** `Transport/Ble/`, библиотечные tests; существующие protocol limits.
Новые имена/папки — предлагаемое место реализации; проверить актуальные исходники.

## Шаги

1. Проверять Ready, непустой кадр и лимит выбранной прошивки до GATT write; использовать общий protocol constant, не копию числа в runtime.
2. Сериализовать GATT writes отдельным gate; CancellationToken передавать до начала и в поддерживаемые async ожидания.
3. Передавать ровно logical payload одной WriteWithResponse операцией. Не добавлять 0x3C/0x3E, length или собственный протокол фрагментации.
4. Проверять GattCommunicationStatus и protocol error. Успешный GATT write не считать Companion OK, MSG_SENT или mesh ACK.
5. Если после начала write результат неизвестен из-за отмены/timeout/disconnect, fault сессию и не повторять запись. Позднее завершение не должно оживлять её.

**Не входит:** Автоматическое дробление кадров, long-write fallback без подтверждения прошивкой, политика повторов сообщений.

## Проверки и завершение

**Тесты/проверки:** Точные bytes; нулевая/предельная/слишком большая длина; конкурентные writes; отмена до gate и после старта; GATT failure; response/ACK приходит до завершения write; отсутствие автоматического повтора.

**Готово:** SendAsync и RX совместно работают через fake; существующий MeshCoreClient может выполнить обмен без изменений runtime.

**Ручная проверка:** Не требуется до BLE8.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; фактические результаты записаны ниже.
- [x] Статус подэтапа, checklist Stage BLE и current-state обновлены.

## Результат выполнения

16.10.2026: после commit/push BLE5 `4d85b30` реализован BLE6.

В Windows asset добавлен public `BleMeshCoreTransport : IMeshCoreTransport` с
существующими options/UI dispatcher. Internal `BleFrameTransport` позволяет проверить
тот же обмен на portable fake. OpenTimeout охватывает подготовку BLE4 и CCCD BLE5
одним общим deadline; Readiness остаётся после успешной подписки. Production DI,
профили, БД и существующий MeshCoreClient/runtime не изменялись.

SendAsync проверяет Ready и длину до native write; firmware limit берётся из
MaximumFrameSize, по умолчанию ProtocolLimits.CurrentFirmwareMaxFrameSize.
Отдельный gate сериализует записи. В Windows один payload передаётся одной
WriteValueWithResultAsync с явным WriteWithResponse, без stream header или дробления.
Native status/protocol error сохраняются в typed exception. Буфер копируется до
записи и не зависит от повторного использования памяти caller после отмены.

Добавлен WriteTimeout (по умолчанию 15 секунд; валидируется как конечный положительный
CTS-compatible interval). Он начинает отсчёт после write gate. Отмена до gate/native
вызова не посылает кадр; отмена после старта, timeout и disconnect не дают обещания,
что устройство не получило кадр: сессия закрывается, RX получает terminal outcome,
новые writes запрещены. Отменённый caller получает OperationCanceledException с
исходным token; terminal RX diagnostics — WriteOutcomeUnknown. Позднее завершение
наблюдается, не повторяет кадр и не возвращает Ready. Native ресурсы не уничтожаются
под pending I/O: Windows connection ждёт завершения операции перед их освобождением.

| Проверка | Результат |
| --- | --- |
| `dotnet restore MeshCoreSharp.sln --locked-mode` | Успешно |
| `dotnet build MeshCoreSharp.sln -c Release --no-restore` | 0 warnings/errors |
| `dotnet run --project tests/MeshCoreSharp.Tests -c Release --no-build` | **152/152** |
| `dotnet restore MeshCoreSharp.sln -p:McmWindowsDesktop=false --locked-mode` | Успешно |
| `dotnet build MeshCoreSharp.sln -c Release -p:McmWindowsDesktop=false --no-restore` | 0 warnings/errors |
| `dotnet run --project tests/MeshCoreSharp.Tests -c Release -p:McmWindowsDesktop=false --no-build` | **150/150** |

Девять новых cases: Ready/длина/timeout validation, точные bytes (включая 1 и 176),
конкурентные writes и queued cancellation, отмена после старта и late success,
deadline и late fault, GATT failure с native metadata, disconnect при write,
общий open deadline с pending CCCD, реальный MeshCoreClient поверх fake BLE.
Интеграционный case выполняет APP_START, SEND_TXT_MSG с MSG_SENT и ACK до завершения
GATT write, затем GET_DEVICE_TIME: успешная native запись сама не завершает команду,
unknown push между write и ответом не ломает обмен. Автоматических повторов нет.
Существующие TCP/Serial regression cases прошли в тех же suites.

Этот checkpoint ещё не завершает lifecycle: транспорт пока рассчитан на одну
connection attempt на экземпляр. Полный контракт повторного Connect того же объекта,
общий shutdown barrier, bounded best-effort CCCD disable и расширенная матрица гонок
остаются BLE7. Disconnect/Dispose терминально закрывают logical I/O, физический
cleanup может закончиться позднее при pending Windows operation; это не обещание
мгновенного аппаратного disconnect. Production включение остаётся после BLE8/BLE12.
Реальная нода и native macOS не проверялись. Полные Core/Desktop suites не запускались;
известное отдельное ограничение Desktop suite сохраняется.

По запросу пользователя **на BLE7 не переходили**.

Источник Windows API: [Microsoft GATT client](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client).
