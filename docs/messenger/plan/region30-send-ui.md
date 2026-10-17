# REGION30 — Composer и сведения о scope отправки

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: реализован17.10.2026.** Scope виден в header по согласованному UX; новые history controls исключены пользователем.

**Цель:** Отобразить budget и scope исходящей попытки по согласованному UX.

**Зависимости:** REGION29. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION29](region29-discovery-ui.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** Desktop composer/message details/repeat owners; Core REGION19–21 read API.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-gui-decisions.md; REGION19/20/21 results; актуальные history/draft contracts.

## Шаги

1. Подключить target-aware budget без дублирования формулы; обновлять при policy/default/transport revision.
2. Показать outgoing scope snapshot и unknown/legacy состояние; входящим не присваивать регион выбранного канала.
3. Подключить утверждённые repeat/as-new состояния, Accepted+cleanup failure и action availability, не меняя retry counts и draft ownership.

**За границами шага:** Новый delivery статус 'доставлено региону', автоматический trim и пересоздание истории.

## Проверки и завершение

**Обязательные проверки:** Draft over-limit после смены scope; stale async result; channel switch; known→flood retry; legacy history; accepted cleanup failure без повторного TX; light/dark/keyboard.

**Готово:** Пользователь видит согласованные ограничения и фактические данные попытки.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Composer подключён к target-aware ReadBudgetAsync, post-commit policy/default refresh и stale revision guard. Draft сохраняется при shrink budget; unsupported scope блокирует send. Header/menu/native4/4, budget/menu/stale4/4; startup regression60/60 после удаления раннего listener dispatch. Existing repeat/as-new/cleanup semantics не менялись. [Отчёт](../../testing/region-desktop.md). Следующий этап REGION31: полный Desktop прогон и фиксация native/fake результатов.

Native проверки и ограничения: [region-desktop.md](../../testing/region-desktop.md). Пользовательский просмотр остаётся отдельной проверкой.
