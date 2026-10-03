# Меню переписки при инициализации workspace — 04.10.2026

Первый этап подготовки ручного сброса маршрута. Сам ResetPath не реализован.

Каждый ChatWorkspaceViewModel при создании получает тип Channels/Personal и создаёт
собственный ConversationMenuViewModel. Items — неизменяемый список моделей пунктов
с командами, созданными один раз; переключение переписки не пересоздаёт меню.

Public: «Подробности (тест)», «Поиск (тест)», «Поделиться (тест)»,
«Удалить историю сообщений».
Private дополнительно содержит «Задать маршрут (тест)» и «Сбросить маршрут (тест)»
перед очисткой. Все тестовые команды — no-op; никаких обращений к ноде.

ConversationView отображает ItemsSource через MenuItem ControlTheme, сохраняя
прежнее положение выпадающего списка и тематическую иконку. Бизнес-действия
привязаны к ICommand, отдельный Click для очистки удалён. Команда очистки захватывает
проверенный актуальный target при нажатии и запрашивает показ диалога. View отвечает
за модальный UI; после подтверждения вызывается исходный HistoryClearViewModel
с зафиксированным target, даже если контекст позже изменится. Без подтверждения
удаления нет. На attach/detach View подписывается/отписывается от запроса диалога.

При composition root wiring существующий HistoryClearViewModel привязывается к
меню, при замене старая подписка снимается. Доступность команды/tooltip обновляются
из CanClear/AvailabilityMessage; открытие меню обновляет availability. Старый ответ
после смены selection не разрешает очистку чужой переписки.

## Проверка

- Release build tool/Desktop/test dependencies: 0 warnings, 0 errors.
- Desktop Release: **294/294**, включая 3 новые проверки различий Public/Private,
  текущей цели/stable commands/обязательного подтверждения и availability/subscriptions.
- Native Release `--history-clear-only`: все 8 сочетаний Public/Private × Light/Dark
  × 420/960. Проверяются реальные MenuItem контейнеры, 4/6 пунктов, отсутствие/наличие
  маршрутного пункта, Command binding, cancel/confirm, empty history и сохранность draft.
  Проверка вызывает связанную команду созданного MenuItem.
- Native harness адаптирован с фиксированных MenuItem в flyout.Items к контейнерам
  ItemsSource. Первый адаптационный запуск не нашёл Popup среди полей; после перехода
  к свойству Popup повтор прошёл. Production код при этом не менялся.
- Screenshots: `$TMPDIR/meshcore-history-clear/` (confirmation/empty history).
- `git diff --check` пройден. Только временная SQLite, без аппаратной ноды/пользовательской БД.

Дальнейший этап: [Core/library reset-route mechanism](../messenger/plan/private-route-reset.md).
