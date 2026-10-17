# REGION31 — Сквозная автоматическая приёмка Desktop

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: автоматическая приёмка выполнена17.10.2026.**

**Цель:** Проверить совместную работу GUI, Core и persistence.

**Зависимости:** REGION30. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION30](region30-send-ui.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** tests/MeshCoreMessenger.Desktop.Tests; docs/testing/region-desktop.md (создать).
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-gui-decisions.md; region-design.md: «Сквозные критерии приёмки».

## Шаги

1. Пройти fake full flow: catalogue→channel preference→send/repeat→history, default edit/readback и discovery→save.
2. Проверить startup/offline/reconnect, switch node, stale results и shutdown с незавершённым discovery/save.
3. Выполнить Desktop suite и Release builds поддерживаемых targets; native visual checks записать отдельно от unit/fake результата.

**За границами шага:** Объявление ручной/аппаратной приёмки по fake результатам.

## Проверки и завершение

**Обязательные проверки:** Keyboard/focus, light/dark, narrow layout, history/draft/unread regression, storage failure, accepted cleanup failure, отсутствие duplicate TX.

**Готово:** Все автоматические слои прошли; перечень аппаратной и пользовательской проверки подготовлен.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Полный Desktop425/425, изолированные Release builds без warnings/errors, native Light/Dark×560/1040 —4/4. Пройдены каталог/consent/discovery save/node isolation, channel policy/header/history/draft, budget shrink и stale readiness; новые UI используют протестированные Core default/readback и send/repeat contracts. Первый полный прогон выявил лишний pre-Load region listener dispatch; исправлен и подтверждён повторным полным прогоном. [Подробный отчёт](../../testing/region-desktop.md).

Пользовательский просмотр нового UI и аппаратная проверка GUI→CatCore не выполнялись. Предыдущие аппаратные sample-проверки не закрывают все REGION32/33; их оставшиеся требования сохранены.
