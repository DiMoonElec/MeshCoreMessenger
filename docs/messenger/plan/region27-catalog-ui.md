# REGION27 — GUI каталога регионов

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: реализован17.10.2026; native/theme/keyboard приёмка остаётся REGION31.**

**Цель:** Реализовать согласованные действия локального каталога.

**Зависимости:** REGION26. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION26](region26-gui-design.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** Desktop/ViewModels/, Views/, DI; Desktop tests.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-gui-decisions.md (создаётся REGION26); REGION15 service contract; architecture/ui-and-local-writes.md.

## Шаги

1. Добавить согласованный catalogue viewmodel и UI через RegionService: добавить, изменить label, archive/replace references.
2. Сохранять node context, offline доступность, post-commit updates и error state; не выполнять SQL/raw commands в VM.
3. Проверить в существующем preview/fake harness темы, narrow layout, keyboard и ошибки ввода.

**За границами шага:** Default ноды, discovery экран и редактирование region key в диагностических логах.

## Проверки и завершение

**Обязательные проверки:** Fake catalogue CRUD; storage failure; switch node во время save; referenced delete; Unicode byte validation; light/dark и keyboard.

**Готово:** Локальный каталог доступен по согласованному UX и сохраняется после reopen.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [ ] Изменения в границах подэтапа выполнены.
- [ ] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [ ] Ограничения и оставшиеся проверки указаны.
- [ ] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Карточка локального каталога Settings использует RegionService: public/import, rename/archive/delete, node context и post-commit reload. Приватный ввод очищается при смене ноды/успешном импорте, не логируется. Referenced delete показывает пояснение Core отказа. RegionUiTests+AppBootstrap8/8; изолированная Release build0 warnings/errors. Обычная Release папка заблокирована открытым пользовательским приложением; оно не закрывалось. Ручная визуальная приёмка остаётся REGION31. Далее REGION28.
