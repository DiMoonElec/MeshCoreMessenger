# P6 — чтение истории успешных доставок и route provenance

Дата: 05.10.2026. [План](../messenger/plan/d7-private-auto-retry.md).

Добавлен IContactDeliveryHistoryReader, доступный через LocalStorage.ContactDeliveries
и Desktop DI. GetPageAsync принимает NodeId, полный ContactPublicKey, limit 1…100
и node/contact-scoped cursor. Сортировка newest-first по FirstAckReceivedUtc/DeliveryId,
page и evidence/candidates читаются в одной SQLite read transaction. Курсор с чужой
identity отвергается; лишняя строка определяет NextCursor без потери равных времён.
API возвращает данные без текста/секретов, отдельно каждый ACK и все candidates:
wire timestamp/attempt/phase, PC send time/offset/zone, configured route и MSG_SENT mode.
Legacy не получает выдуманные candidates/маршруты/время отправки.

ConfiguredBeforeSend — наблюдённый маршрут перед TX. ContactReadbackAfterAcknowledgement —
контакт, прочитанный после commit конкретного ACK. Это актуальный путь для будущих
передач, а не доказанная трасса доставки уже подтверждённого пакета. Учитывается
неоднозначность ACK: several candidates остаются several candidates, learned route
не выбирает успешную попытку задним числом.

Успех и ACK evidence по-прежнему пишутся атомарно через общий matcher P3. После
первой вставки каждого evidence commit публикует точный EvidenceId/NodeId/SessionId/
ContactPublicKey. Durable writer передаёт запрос session-owned readback queue.
Push pump только ставит запрос, не ждёт GetContact; используется текущая очередь
обновления маршрутов, не создаётся новый транспортный reader или отправка.

Typed GetContact и локальная актуализация дают route snapshot, который сохраняется
к одному evidence через durable outgoing writer. Первый snapshot не перезаписывается
дубликатом ACK/SQL retry. Каждый дополнительный ACK tag имеет собственный readback.
Owner проверяется по evidence/node/session/full key. Запись с уже сохранённым
snapshot идемпотентно возвращает false. SQL retry сохраняет снимок, не перечитывая
контакт и не передавая сообщение. Нет readback replay при reconnect/startup.

Ошибка/отмена readback оставляет LearnedRoute=null, не отменяя Delivered. Ошибка
SQL enrichment удерживает снимок в outgoing queue и включает persistence pause;
после recovery повторяется только SQL. Очередь принадлежит исходной session и
поддерживает late ACK после полного исчерпания radio retries. PATH_UPDATED и ACK
могут вызвать отдельные readback; прежние тесты количества запросов обновлены.

Миграция не нужна: используется существующая schema v5 с history/evidence/candidates
и nullable LearnedRouteDescriptor/Path/ObservedUtc. Provenance выражен типизированным
DTO и назначением snapshot, не смешивается с Contacts. Время ПК хранится без
искусственного выравнивания с wire timestamp: ACK/readback могут иметь более ранние
wall-clock даты из-за перевода часов. Порядок commit обеспечивает причинность.
FirstAck сохраняет offset/timezone первого успеха, candidate — offset/timezone
своей передачи; последующее evidence имеет собственное UTC время ACK.

Retention: clear-history удаляет текст/attempts, MessageId/AttemptId становятся
null, независимые snapshots/evidence остаются. Данные читаются после удаления
переписок/контактов и reopening базы. NodeId является владельцем всей аналитики;
удаление собственной ноды может каскадировать её историю. UI графиков/процентов
успеха отсутствует; список успешных доставок сам по себе не даёт success rate.

Проверено SQLite и TCP emulator:

- cursor tie-break/scoping/page limit и пустой список чужой ноды;
- PC send/ACK timestamps, offset/timezone, перевод часов назад, UInt32 RTT;
- immutable readback и отдельные snapshots дополнительных ACK;
- ambiguous candidates разных known routes, legacy без fabricated metadata;
- flood ACK → learned path readback при прежнем configured flood;
- readback failure сохраняет Delivered/null route;
- late ACK после трёх timeout получает snapshot без нового TX;
- SQL enrichment failure/retry сохраняет наблюдение без дополнительных команд;
- clear/conversation/contact removal/reopen сохраняют независимую аналитику.

Аппаратные дальние испытания ещё предстоят пользователю. P7 (incoming dedup и
итоговые UI projections) не начат. Хэши пакетов остаются отложенными.

Release build: 0 warnings/errors. Library 105/105, Core 354/354, Desktop 318/318.
