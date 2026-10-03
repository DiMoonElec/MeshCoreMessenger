# D14 — UI локальной очистки истории

[Stage D](stage-d.md) · **Статус: не начат.**

**Цель:** отличить локальную очистку от удаления contact/channel с ноды.

**Scope:** отдельное действие выбранной переписки, confirm с нодой/чатом и
описанием удаления messages/attempts. Не удалить аккаунт/папку. Empty/confirm/cancel/
busy/write failure preview; по умолчанию сохранить contact/channel/draft.
Указать: удаляются уже сохранённые сообщения, поздние входящие могут появиться.
При активном send действие недоступно с объяснением.

**Не входит:** DELETE, очистка приложения, удаление peer на ноде, bulk operations.
Runtime action disabled. **Зависимости:** D7, D13; identity/preview harness.

**Тесты:** cancel/no SQL/no wire; immutable target при смене чата;
availability/busy/errors; remove-from-node не путается с clear-local.

**Готово:** последствия понятны, target проверяем, форма готова к D15.
**Ручная проверка: обязательна** — темы/narrow/keyboard cancel-confirm,
long name и отсутствие удаления от первого клика.
