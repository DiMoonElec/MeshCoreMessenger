# BLE13 — Ошибки BLE и существующий reconnect

[Stage BLE](stage-ble.md) · [Оглавление](../../MESSENGER_PLAN.md)

**Статус: запланирован 15.10.2026; реализация не начата.**

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

- [ ] Изменения в границах подэтапа выполнены.
- [ ] Обязательные проверки выполнены; фактические результаты записаны ниже.
- [ ] Статус подэтапа, checklist Stage BLE и current-state обновлены.

## Результат выполнения

Пока отсутствует. При выполнении записать дату, изменения, команды и результаты
проверок, оставшиеся ограничения. Не заменять реальные результаты планом.
