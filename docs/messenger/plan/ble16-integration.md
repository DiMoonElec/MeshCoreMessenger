# BLE16 — Автоматическая приёмка полного пути

[Stage BLE](stage-ble.md) · [Оглавление](../../MESSENGER_PLAN.md)

**Статус: реализован и автоматически проверен 16.10.2026; аппаратная приёмка отдельно в BLE17.**

**Цель:** Проверить BLE-путь приложения без радио и пользовательской БД.

**Зависимости:** BLE15. Проверить результаты зависимостей перед началом.
Общие инварианты и решения — в [Stage BLE](stage-ble.md).

**Файлы/область:** Существующие library/Core/Desktop tests и их fake adapters; отчёт `docs/testing/ble16-integration.md` создать при выполнении.
Новые имена/папки — предлагаемое место реализации; проверить актуальные исходники.

## Шаги

1. Собрать сценарий fake GATT → BleMeshCoreTransport → MeshCoreClient → CompanionSession → supervisor/ingest на временной SQLite.
2. Проверить Identify по полному ключу, contacts/channel sync, interleaved push, initial drain и post-commit UI.
3. Подключить одну identity через разные профили TCP/Serial/BLE: история общая. Другая identity по тому же BLE endpoint: отдельная история.
4. Проверить drop во время sync/drain/send, отмену открытия, переполнение RX и shutdown barriers. Использовать существующие send semantics для неизвестного результата и позднего ACK.
5. Запустить полную актуальную regression suite library/Core/Desktop после targeted tests; старые checkpoint counts не выдавать за новые результаты.

**Не входит:** Реальные передачи, изменения пользовательской БД, расширение протокольной поверхности.

## Проверки и завершение

**Тесты/проверки:** Каждый перечисленный сценарий; нет duplicate event consumers, replay от reconnect и silently dropped frames; portable compile и Windows graph.

**Готово:** Полный fake путь проходит, результаты и известные ограничения записаны; hardware acceptance не объявлена пройденной.

**Ручная проверка:** Не требуется.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; фактические результаты записаны ниже.
- [x] Статус подэтапа, checklist Stage BLE и current-state обновлены.

## Результат выполнения

16.10.2026: добавлены девять сценариев `BleIntegrationTests` и GATT-эмулятор,
использующий общий реальный BLE transport engine Windows wrapper. Пройдены
Identify/directory/drain/commit notifications, identity/history через разные
профили и изменение последнего байта ключа на том же endpoint, sync/drain/write
drop, отмена open, RX overflow, поздний ACK, automatic reconnect без replay,
channel send и сохранение слишком длинного UTF-8 draft без GATT write.
Существующие suites проверяют дополнительные shutdown/event/ingest barriers.

Core **460/460**, library **179/179 Windows**, **177/177 portable**, полный Desktop
**417/417 Windows** и **417/417 portable** (последовательный режим, включая100000
сообщений). Обе Release solution builds/locked restore без warnings/errors.
Ранее проблемный private UI test также прошёл отдельно3/3. Пользовательская БД,
радио и native macOS не использовались. Поведение TCP/Serial и public API не менялись;
для проверки Serial identity подменён только transport factory.

Known limitation: при overflow общий connection/drain error может опередить
BLE-причину в supervisor; typed overflow сохраняется в BackgroundError, сессия
закрывается. BLE8 RX174–176 и оставшаяся ручная приёмка BLE15 остаются открытыми.
Переход к этому этапу разрешён пользователем после реального GUI connect/sync/send.
Далее BLE17 требует согласованной аппаратной сессии с участием пользователя.

Подробности и команды: [отчёт BLE16](../../testing/ble16-integration.md).
