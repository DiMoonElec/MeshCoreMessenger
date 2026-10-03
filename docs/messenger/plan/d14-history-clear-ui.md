# D14 — UI локальной очистки истории

[Stage D](stage-d.md) · **Статус: реализован 03.10.2026 вместе с D15; ручная приёмка ожидается.**

**Цель:** отличить локальную очистку от удаления contact/channel с ноды.

**Scope:** отдельное действие выбранной переписки, confirm с нодой/чатом и
описанием удаления messages/attempts. Не удалить аккаунт/папку. Empty/confirm/cancel/
busy/write failure preview; по умолчанию сохранить contact/channel/draft.
Указать: удаляются уже сохранённые сообщения, поздние входящие могут появиться.
При активном send действие недоступно с объяснением.

**Не входит:** DELETE, очистка приложения, удаление peer на ноде, bulk operations.
По запросу пользователя runtime action подключён вместе с D15 до D7/D13;
identity/history/draft и отправка D3–D6 уже реализованы.

**Тесты:** cancel/no SQL/no wire; immutable target при смене чата;
availability/busy/errors; remove-from-node не путается с clear-local.

**Готово:** последствия понятны, target проверяем, форма готова к D15.
**Ручная проверка: обязательна** — темы/narrow/keyboard cancel-confirm,
long name и отсутствие удаления от первого клика.

Результат и проверки: [отчёт очистки](../../testing/history-clear.md).
