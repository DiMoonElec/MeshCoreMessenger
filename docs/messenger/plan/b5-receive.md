# B5 — ReceiveCoordinator и безопасный начальный drain (выполнено 28.09.2026)

[Оглавление](../../MESSENGER_PLAN.md) · [Маршрутизация чтения](../../README.md)

#### B5 — ReceiveCoordinator и безопасный начальный drain (выполнено 28.09.2026)

**Цель:** связать B2–B4 в одну синхронизацию `identify -> directories -> drain ->
commit`, не допуская параллельных или небезопасных чтений очереди Companion.

- `ReceiveCoordinator` подписывается до Start, но при `AutoReceiveMessages=false`
  использует `PushPacketReceived` только как сигнал `PacketType.MessagesWaiting`.
  Не дублировать один сигнал через `PacketReceived` и `PushPacketReceived`.
- После B3 выполнить начальный `DrainMessagesAsync`. После его возврата вызвать
  `MeshCoreClient.FlushEventsAsync`, затем `MessageIngestor.FlushAsync`; только после
  обоих барьеров считать backlog закоммиченным и активировать pending channel bindings.
- Сигналы `MessagesWaiting`, пришедшие во время identify, чтения справочников или
  текущего drain, объединять в один pending flag. После завершения прохода выполнить
  ещё один drain, если сигнал был поставлен; одновременно работает не более одного.
- После тайм-аута `SYNC_NEXT_MESSAGE` текущая сессия считается непригодной: не
  повторять drain на том же client, завершить попытку и передать решение B6. Ошибка
  радио-ACK к входящему pipeline отношения не имеет.
- При ошибке ingest остановить новые проходы, сохранить pending signal/DTO и перейти
  в `NeedsAttention`; восстановление writer сначала дописывает память, затем разрешает
  новый drain. `Online` публикуется только после справочников, начального drain,
  event barrier, commit barrier и активации bindings.

Обязательные тесты B5: сообщения во время Start и загрузки справочников; сигнал во
время drain и гонка около `NO_MORE_MESSAGES`; несколько сигналов дают один дополнительный
проход; callbacks задержаны после возврата drain; changed-slot backlog остаётся
неопределённым, а следующий проход использует новую binding; timeout не повторяется
в старой сессии; ошибка БД останавливает чтение без потери уже принятых DTO.

Реализовано: `ReceiveCoordinator` является единственным consumer событий
`CompanionSession`. Сессия подписывается на public client events до `StartAsync`, а
coordinator обрабатывает только скопированные session events; из packet-событий он
использует исключительно push `MessagesWaiting`. Сообщения, пришедшие до завершения
справочников, удерживаются в памяти. После directory snapshot initial drain выполняется
один раз, затем coordinator ждёт library `FlushEventsAsync`, marker application event
queue и `MessageIngestor.FlushAsync`; только после этого активируются pending channel
transitions. Сигнал в этом окне создаёт дополнительный проход с новой stable binding.
Сигналы во время последующих проходов coalesce до одного pending flag; параллельных
drain нет. Timeout `SYNC_NEXT_MESSAGE` переводит coordinator в `NeedsAttention` и
запрещает повтор на том же client. Ошибка ingress приостанавливает новые drain до
явного `RetryAsync`, который сначала дописывает уже принятые DTO.

Проверено: fake Companion/SQLite tests покрывают сообщение, удержанное с `StartAsync`,
coalesced `MessagesWaiting`, timeout без retry, pause/retry storage и changed channel
slot: backlog сохранён как unknown, следующий проход получает новую binding. TCP/Serial,
физическая нода и RF-команды не использовались.
Release build прошёл без предупреждений; Core tests — 61/61, Desktop — 18/18,
MeshCoreSharp regression suite — 92/92.
