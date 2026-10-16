# REGION25 — Сквозная приёмка Core

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: не начат.** Задание подготовлено 16.10.2026.

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

- [ ] Изменения в границах подэтапа выполнены.
- [ ] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [ ] Ограничения и оставшиеся проверки указаны.
- [ ] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Ожидает выполнения. Следующий шаг после завершения — [REGION26](region26-gui-design.md).
