# REGION16 — Capabilities и чтение default в сессии

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 16.10.2026.** Задание подготовлено 16.10.2026.

**Цель:** Read-only подключение library API к Core и startup.

**Зависимости:** REGION15. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION15](region15-catalog-service.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** ICompanionClient, MeshCoreClientFactory, CompanionSession, ConnectionAttempt и session gateway.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Владение состоянием и модель», «Совместимость»; architecture/connections.md и shutdown.md.

## Шаги

1. Добавить typed adapter/fake seams для region methods, без raw client в Core/UI; capabilities хранить по конкретной session.
2. Встроить read-only default refresh после identification и до открытия зависящего send admission; сохранить snapshot с NodeId/SessionId/generation.
3. Обработать Unsupported как отсутствие конкретной функции, Unknown отдельно; late/no-correlation timeout передать existing session lifecycle. Connect не вызывает SET/RESET/discovery.

**За границами шага:** Automatic configuration replay и UI default editor.

## Проверки и завершение

**Обязательные проверки:** Legacy firmware unsupported; KnownEmpty/Unknown; другая нода по endpoint; reconnect c внешним изменением default; stale result; shutdown; initial message drain.

**Готово:** Новая сессия получает честный capability/default snapshot без мутации ноды.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Реализованы typed adapter seams, session-specific capabilities и read-only GET default после identification до открытия команд. Snapshot содержит NodeId/SessionId/generation; постоянный кеш читается как Offline. Unsupported относится только к конкретной функции, неизвестная поддержка не подменяется успешной. Timeout/отмена завершают сессию существующим lifecycle. Подключение не вызывает SET/RESET/discovery.

Проверки: новые RegionSessionTests покрывают admission, KnownEmpty/Unknown/Unsupported, reconnect, другую ноду на endpoint, поздний ответ после отмены и failed-start teardown. BLE integration проверяет отсутствие мутаций на startup. Release solution build: 0 warnings/errors; Desktop AppBootstrapTests: 6/6. Полный Core: 485/485 (`dotnet run --project tests/MeshCoreMessenger.Core.Tests -c Release --no-build -- -noLogo -parallelMode none`). Отдельный BLE integration: 9/9. Синтетические downgrade fixtures очищают также объекты schema8.

Аппаратные проверки не выполнялись; они остаются REGION32–33. По запросу пользователя работа остановлена после REGION16; следующий шаг — [REGION17](region17-default-service.md).
