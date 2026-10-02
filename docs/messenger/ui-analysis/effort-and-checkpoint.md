# Оценка объёма и затрагиваемые файлы

[Оглавление](../../UI_COMPONENTS_AND_REDESIGN_ANALYSIS.md) · [Маршрутизация чтения](../../README.md)

## Оценка объёма и затрагиваемые файлы

Для планирования: четыре отдельные агентские сессии реализации плюс ручные проверки
между ними и существующий C10. Это ориентир granularity, не обещание календарного
срока: UI1/UI2 могут потребовать дополнительной итерации из-за viewport/focus.
Визуально переложить всё можно быстрее, но подтверждение сохранности текущих
сценариев составляет значительную часть объёма.

Ожидаемые изменения:

- `Desktop/Views/MainWindow.axaml(.cs)` — shell вместо деталей всех screens;
  новые views в `Views/Chat`, `Views/Settings`, `Views/Devices`, `Views/Shell`.
- `MainWindowViewModel`, `ConversationNavigationViewModel` — section coordination
  и явный visible context; History/Draft VM — лишь необходимые lifecycle hooks.
- `Views/DesktopShortcutRouter.cs` — routing по активному workspace.
- `App.axaml` и новые shared styles/resources — оформление и vector icons.
- `Bootstrap/AppBootstrap.cs` — регистрация новых VM только при необходимости;
  компоненты не создают новый supervisor/storage.
- `Desktop.Tests` — visibility/navigation/ownership/shutdown regressions;
  `docs/testing` — ручная приёмка, после согласования — актуализация плана/архитектуры.

Core и MeshCoreSharp по текущим требованиям менять не нужно. Отдельные сборки для
компонентов, новая SQLite schema, Prism/ReactiveUI/navigation framework не нужны.
Удалённая информация о ретрансляторе является отдельной функциональностью вне
первого редизайна, если под «информацией» подразумеваются новые Mesh-запросы.

## Проверка исходной точки

Перед коммитом C9 выполнены Release build (0 warnings/errors), Core 127/127,
Desktop 111/111, MeshCoreSharp 92/92 и `git diff --check`.
C9 зафиксирован отдельным коммитом `dfb9648`.
Служебные изменения `.DS_Store` не включались в коммит и не удалялись.
Production/test-код при подготовке этого анализа не изменялся.
