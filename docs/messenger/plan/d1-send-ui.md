# D1 — UI отправки, исходящих и статусов

[Stage D](stage-d.md) · **Статус: не начат.**

**Цель:** согласовать визуальный контракт до подключения эфира.

**Scope:** Composer с местом для UTF-8 счётчика/ошибок и Send; исходящий пузырь,
время и текстовые статусы Prepared/Sending/AwaitingAck/Delivered/AcceptedByNode/
Unconfirmed/Failed/Unknown. Просмотр попытки и явный повтор с предупреждением
дубликата. Preview показывает все состояния на тех же controls, без записей в БД.
Составной composer presentation owner использует существующий draft owner, не
переносит его сохранение в view. Два chat workspace остаются независимыми.

**Не входит:** настоящий подсчёт лимита, отправка, outgoing SQL, изменение Core/schema,
session API, замена history/scroll. Обычный Send остаётся выключенным с пояснением.
Макет счётчика — только demo; не дублировать encoder для production валидации.
**Зависимости/чтение:** [baseline/preview](stage-d-start.md), UI2, существующие
ComposerView/ConversationView/HistoryMessageListItem; sending architecture.

**Тесты:** runtime заглушка не вызывает client/store; preview всех статусов;
bindings/resources обеих тем; draft/copy/mentions/public-private round trip;
доступность кнопок, понятность статусов без одного лишь цвета.

**Готово:** воспроизводимый preview, компоненты готовы к D2/D5; обычный запуск
ничего не отправляет. В отчёте — команда preview и список demo-состояний.
**Ручная проверка: обязательна** — две темы, narrow/wide, длинные имена, текст/emoji,
пузыри, статусы, disabled Send и предупреждение повтора.
