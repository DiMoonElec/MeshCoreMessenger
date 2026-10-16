# BLE13 — Ошибки BLE и существующий reconnect

[Stage BLE](stage-ble.md) · [Оглавление](../../MESSENGER_PLAN.md)

**Статус: реализован 16.10.2026.**

**Цель:** Сопоставить новые причины отказа с состояниями приложения.

**Зависимости:** BLE12. Проверить результаты зависимостей перед началом.
Общие инварианты и решения — в [Stage BLE](stage-ble.md).

**Файлы/область:** `Core/Application/ConnectionFailureClassifier.cs`, supervisor tests; библиотечные typed exceptions при необходимости.
Новые имена/папки — предлагаемое место реализации; проверить актуальные исходники.

## Шаги

1. Unsupported platform, invalid endpoint, pairing/access required, missing service/несовместимый MTU классифицировать NeedsAttention.
2. Временную недоступность, Bluetooth off и потерю соединения классифицировать Transient; не обходить reconnect=false и ручной Disconnect.
3. Проверить, что BLE typed причины анализируются до общего MeshCoreTransportException. PIN/consent не запрашивать в бесконечном background retry.
4. Использовать существующие backoff, generation, sleep/resume и heartbeat. Не добавлять второй цикл reconnect в transport/provider.
5. Сохранить существующую send/retry policy: факт BLE reconnect сам не инициирует повтор исходящих или изменение конфигурации.

**Не входит:** Переработка supervisor state machine, изменение алгоритма автоматических повторов ЛС.

## Проверки и завершение

**Тесты/проверки:** Error mapping; adapter off/on; reconnect=false; ручное отключение; switch profile во время open; late error старой generation; shutdown; отсутствие отправок от reconnect.

**Готово:** UI получает понятную причину и правильное действие; один supervisor управляет всеми попытками.

**Ручная проверка:** Off/on и sleep проверяются на железе в BLE17.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; фактические результаты записаны ниже.
- [x] Статус подэтапа, checklist Stage BLE и current-state обновлены.

## Результат выполнения

16.10.2026: ConnectionFailureClassifier разбирает MeshCoreBleTransportException до
общего MeshCoreTransportException. AdapterUnavailable, RadioOff, DeviceUnreachable,
TransportFailure и WriteOutcomeUnknown — Transient. Остальные typed BLE причины —
NeedsAttention: pairing/access/platform, характеристики/MTU, invalid frame, overflow
и неподтверждённое incoming notification. Для последних двух автоматический reconnect
не восстанавливает уже потерянные данные; повторять такой drain в фоне не следует.
Invalid endpoint остаётся ArgumentException/NeedsAttention. Рекурсивное распознавание
single-inner AggregateException сохранено.

WriteOutcomeUnknown разрешает переподключение, **не повтор отправки**: существующая
политика статусов/ACK/повторов не менялась. Supervisor, provider и transport не получили
новых retry loops, send/config команд. Backoff, reconnect=false, ручной Disconnect,
generation и sleep/resume остаются у прежнего owner. Локализация BLE ошибок редактора
относится к BLE15; typed причины и disposition уже доступны приложению.

`dotnet run --project tests/MeshCoreMessenger.Core.Tests -c Release --
-class MeshCoreMessenger.Core.Tests.ConnectionSupervisorTests` — **59/59**:
все15 typed причин (также nested aggregate), permanent failures без retry,
off/unavailable/lost/unknown-write → backoff → fresh generation, reconnect=false,
ручное отключение; прежние switch/open, late generation, shutdown, power tests сохранены.
Новые тесты ожидают регистрацию fake delay отдельно от публикации RetryWaiting,
поскольку это разные события. Полный Core suite — **446/446**.
Аппаратные off/on и sleep не выполнялись, остаются в BLE17. Следующий этап — BLE14.
