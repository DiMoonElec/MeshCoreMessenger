# D15 — локальная транзакционная очистка

[Stage D](stage-d.md) · **Статус: реализован 03.10.2026; автоматически проверен, ручная приёмка ожидается.**

**Цель:** удалить только явно выбранную историю без поздних UI гонок.

**Scope:** Core service, node/conversation validation, один writer transaction,
capture cutoff LocalSequence. Удалить committed messages до cutoff и attempts;
согласованно обновить read/preview, сохранить directory identity/draft.
Сериализовать с prepare, запретить clear при незавершённых send/delivery/durable-retry
tasks этой переписки. Post-commit invalidate history/search/anchor; late read не
возвращает удалённое. Сообщения после cutoff остаются; protocol commands отсутствуют.

**Не входит:** удаление ноды/профилей/секретов, других чатов, VACUUM/secure erase.
**Зависимости:** реализованные D3–D6, D14; DatabaseWorker/read/draft/history contracts.
По запросу пользователя выполнен до D7; будущий ручной повтор обязан участвовать
в ConversationOperationGuard и блокировать очистку до durable terminal completion.

**Тесты:** transaction failure → всё сохранено; другая нода/чат неизменны;
FK/cascade attempts; cutoff с incoming commit; pending send/ACK/write retry;
read/unread/search/draft/late pages; reopen/workspaces; zero wire.

**Готово:** UI D14 wired, другие targets неизменны.
**Ручная проверка: обязательна** — disposable fixture, confirm/cancel/restart,
incoming после clear. Настоящие данные для теста не удалять.

Результат и проверки: [отчёт очистки](../../testing/history-clear.md).
