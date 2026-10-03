# D3 — durable исходящие и startup recovery

[Stage D](stage-d.md) · **Статус: не начат.**

**Цель:** до эфира сохранить сообщение, попытку и факт возможного начала вызова.

**Scope:** outgoing store Core/LocalStorage; атомарный Prepare сообщения и первой
попытки, node/recipient ownership, idempotent operation ID. Durable Sending/переходы,
immutable attempts/history projection, post-commit notifications отдельно от incoming.
Минимальная миграция по [baseline](stage-d-start.md), без изменения старых enum values/
данных. Startup recovery Sending/AwaitingAck → Unknown; Prepared не отправляется.
Хранить text, attempt metadata и binding, не секрет канала. Определить отображение
legacy Accepted/NotExpected.

**Не входит:** radio API, ACK observer, manual retry, общее изменение schema.
Никакой записи mock Delivered в fixture через прямой SQL.
**Зависимости:** D2; storage/sending, DatabaseMigrator/HistoryModels/LocalStorage.

**Тесты:** prepare атомарен при сбое; operation ID не дублирует message;
wrong node/recipient; CAS/terminal ordering; migrated v2 reopen, backup/FK/index/
rollback; recovery всех состояний, zero TX; outgoing не увеличивает unread;
bounded projection без N+1; уведомления только после commit.

**Готово:** crash distinction proven SQLite тестами, schema changes обоснованы;
store готов к D5, production UI ещё не отправляет.
**Ручная проверка:** preview проекции полезен; проверки эфира не требуются.
