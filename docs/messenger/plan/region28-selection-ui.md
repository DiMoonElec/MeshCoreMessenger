# REGION28 — GUI default ноды и выбора канала

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: реализован17.10.2026; native приёмка REGION31 pending.**

**Цель:** Подключить три режима канала и online настройку default.

**Зависимости:** REGION27. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION27](region27-catalog-ui.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** Desktop channel details/settings owners по REGION26; Core REGION17/19 contracts.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-gui-decisions.md; REGION17 и REGION19 results.

## Шаги

1. Показать InheritDefault / ExplicitRegion / ForceUnscoped там, где согласовано; channel save остаётся локальным.
2. Подключить read/SET/CLEAR default ноды с readback outcome и freshness; различать offline/unsupported/unknown.
3. Обновить отображение после commit и при смене active node; не добавлять автоматический SET при открытии экрана.

**За границами шага:** Per-contact scope UI и автоматическое изменение radio presets.

## Проверки и завершение

**Обязательные проверки:** Канальная настройка offline; default online; mismatch/timeout; reconnect; duplicate slots; переключение ноды во время команды; keyboard и темы.

**Готово:** Пользователь различает локальную политику канала и реальную настройку ноды.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [ ] Изменения в границах подэтапа выполнены.
- [ ] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [ ] Ограничения и оставшиеся проверки указаны.
- [ ] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Settings содержит отдельную карточку default: local catalogue selection, explicit SET/CLEAR/readback, freshness/unsupported/unknown. Заголовок канала показывает эффективное значение; ⋯ → «Регион отправки» содержит inherit/unscoped/catalogue, CAS сохранение по stable ChannelId без изменения history/draft/slots. Startup не выполняет мутации. Регрессия offline channel menu/history/draft прошла1/1; catalogue/default/bootstrap9/9; изолированная Release build0 warnings/errors. Manual/theme/keyboard приёмка REGION31 pending. Далее REGION29 и budget wiring REGION30.
