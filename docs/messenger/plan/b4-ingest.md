# B4 — durable MessageIngestor и транзакции входящих (выполнено 28.09.2026)

[Оглавление](../../MESSENGER_PLAN.md) · [Маршрутизация чтения](../../README.md)

#### B4 — durable MessageIngestor и транзакции входящих (выполнено 28.09.2026)

**Цель:** любой уже доставленный приложению `ReceivedMessage` либо надёжно ожидает
записи, либо закоммичен в правильный диалог; callback библиотеки не выполняет SQL
и не блокирует RX.

- Перед кодом провести schema audit. Текущей migration 1 не хватает полей публичных
  моделей библиотеки: добавить нумерованную транзакционную migration для nullable
  `TextType`, `PathLength`, `BinaryDataType` и отдельного 4-byte
  `OriginalSenderPrefix`. Существующие `OriginalPublicKeyPrefix` (6 bytes),
  `OriginalChannelSlot`, `WireTimestamp`, SNR и payload сохранить. Обновить модели
  и migration/backup/regression tests; старые строки должны открываться безопасно.
- Реализовать application ingress queue с одним consumer и явным `FlushAsync`.
  Синхронный `MessageReceived` callback только копирует строки/массивы в неизменяемый
  DTO, назначает новый `EventId`, SessionId/NodeId и ставит его в очередь. Одинаковые
  text/timestamp считаются разными событиями; повтор того же DTO с тем же EventId
  идемпотентен за счёт unique constraint.
- Для личного сообщения искать все текущие контакты с полным совпадением 6-byte
  prefix: один кандидат — диалог полного ключа и `Resolved`; ноль — временный
  `UnknownContact/Unresolved`; больше одного — тот же безопасный временный диалог и
  `Ambiguous`. Не выбирать первый результат и не разрешать по имени.
- Канальное сообщение разрешать только через mapping текущей фазы синхронизации.
  Stable binding даёт Channel conversation; изменённый/неизвестный slot — отдельный
  `UnknownChannel`. Для неоднозначного backlog `UnknownIdentity` должен включать
  SessionId/transition и slot, чтобы несвязанные смены конфигурации не склеивались.
- Одной SQLite-транзакцией создать/найти conversation, вставить `Messages`, обновить
  `Conversations.UpdatedUtc` и необходимые read projections. UI-сигнал публиковать
  только после commit и заставлять UI перечитать проекцию, а не доверять callback.
- При disk-full/read-only/ошибке writer не удалять DTO и следующие события: поставить
  ingest на паузу, сообщить `NeedsAttention`, запретить новые drain и дать явный retry.
  Добавить сигнал перегрузки около 1000 сообщений или 16 MiB; пользовательские
  сообщения не отбрасывать и не применять DropOldest/DropWrite.

Обязательные тесты B4: все три публичных типа входящих сообщений и их поля;
однозначный/отсутствующий/коллидирующий prefix; stable/changed/unknown channel slot;
одинаковые эфирные тексты сохраняются отдельно; повтор одного EventId не дублируется;
commit предшествует UI notification; callback не ждёт SQLite; порядок последовательного
writer; cancellation/flush; disk error удерживает pending DTO и успешный retry их
записывает. Проверить upgrade БД v1 -> v2 и backup/restore.

Реализовано: migration 2 расширяет `Messages` nullable полями `TextType`,
`PathLength`, `BinaryDataType` и 4-byte `OriginalSenderPrefix`; ранее созданная
v1 БД обновляется транзакционно, без пересоздания. `MessageIngestor` принимает
скопированные DTO в неограниченную FIFO-очередь с одним consumer и explicit
`FlushAsync`; его callback не ждёт SQLite. При ошибке writer текущий DTO остаётся
первым в очереди, ingress ставится на паузу до `RetryAsync`, а новые события
сохраняются в памяти. Сигнал перегрузки появляется от 1 000 DTO или примерно
16 MiB; очередь не применяет политику drop. SQLite store проверяет EventId до
изменения проекций, поэтому повтор не создаёт даже пустой conversation; новая
доставка с тем же текстом и временем имеет новый EventId и записывается отдельно.
Private prefix разрешается только при ровно одном текущем контакте; stable channel
binding проверяется в БД, а неизвестный или изменённый slot получает
session-scoped `UnknownChannel` identity. Commit conversation/message/projection
выполняется одной транзакцией; уведомление `MessageCommitted` приходит после неё.

Проверено: 55 Core tests, включая три модели incoming messages, protocol metadata,
unique/ambiguous/absent private prefix, stable/changed/unknown channel slot,
EventId idempotency, commit-before-notification, копирование callback-буферов,
flush/cancellation, retry после имитированной ошибки writer и миграцию v1 -> v2.
Существующий backup/restore regression выполняется на v2 схеме. Аппаратный порт,
drain и какие-либо передачи не использовались.
