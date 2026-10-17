# REGION25 — Сквозная приёмка Core

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 17.10.2026.**

**Цель:** Library→session→services→временная SQLite без GUI.

**Зависимости:** REGION24. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION24](region24-discovery-service.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** tests/MeshCoreMessenger.Core.Tests; docs/testing/region-core.md (создать).
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Сквозные критерии приёмки»; результаты REGION12–24 при необходимости.

## Шаги

1. Добавить end-to-end сценарии через реальные library encoders/router и fake transport, не только stub service results.
2. Проверить catalogue/default/channel send/private retries/discovery вместе с continuous ingest, reconnect и shutdown barriers.
3. Прогнать Core suite и library regression для затронутых API; записать actual results, remaining hardware limitations и готовые GUI contracts.

**За границами шага:** Реальные передачи и самостоятельный выбор UX.

## Проверки и завершение

**Обязательные проверки:** Две ноды/профили; offline start; storage pause/retry; scope uncertainty; late callbacks; no replay; scopes history; temp route restoration conflict.

**Готово:** Прикладной backend готов к совместному проектированию GUI; данные и lifecycle доказаны.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Добавлены семь сквозных wire cases: catalogue/default/channel/private/discovery, scope sequence и repeat/as-new, ingest во время запроса, restore conflict, malformed/timeout, TableFull, BLE→TCP reconnect с внешним default и изоляцией другой ноды. Использованы реальные library/session/services и временные SQLite; эмулятор расширен CMD9/55/57 и per-send scope evidence. Исправлена гонка существующего BLE overflow test fixture без production изменений.

Release solution build 0 warnings/errors; полный Core **558/558**, library **228/228**, Desktop bootstrap **6/6**. Матрица обязательных сценариев и точные ограничения: [отчёт](../../testing/region-core.md). Hardware/RF не выполнялся; scope firmware-generated PATH/ACK и reboot persistence остаются REGION32/33.

Следующий шаг — [REGION26](region26-gui-design.md), совместный выбор GUI. Backend готов, production GUI не реализуется до согласования.
