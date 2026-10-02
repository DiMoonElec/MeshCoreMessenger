# B7.4 — end-to-end и аппаратная приёмка (выполнено 28.09.2026)

[Оглавление](../../MESSENGER_PLAN.md) · [Маршрутизация чтения](../../README.md)

##### B7.4 — end-to-end и аппаратная приёмка (выполнено 28.09.2026)

- Production-like harness: fake Companion, настоящий supervisor/coordinator/ingestor
  и временная SQLite. Покрыть сообщения во время Start, directory load, reconnect и
  shutdown, отсутствие второго reconnect loop и автоматических передач/mutations.
- После всех fake/regression tests выполнить Serial-приёмку без RF-передач: uptime
  без reset, backlog drain, unplug/replug и два запуска; по возможности проверить TCP
  к той же ноде. Результат записать в `docs/testing/`.

Готово, когда сообщения с fake transport, пришедшие во время запуска/получения
справочников/отключения, записываются в правильную историю; в старте нет автоматических
передач; два reconnect-цикла не возникают. Затем на ноде проверить чтение накопленных
сообщений, два запуска, отсутствие сброса и корректный unplug/replug.

Добавлен production-like fake harness с настоящими
supervisor/session/coordinator/ingestor и временной SQLite. Через две generation он
закоммитил по исходному SessionId восемь сообщений, пришедших во время `Start`, чтения
справочника, initial drain и закрывающего event barrier. Одновременно существовал
ровно один client; фактические вызовы ограничились connect/start/read/drain/barriers/
disconnect/dispose, без send/advert/mutations.

На Serial выполнены no-reset/read-only probes и два полных запуска production
Messenger pipeline с повторным открытием одной временной БД. Uptime вырос 401 -> 404
секунды, radio TX counters не изменились, накопленные 3 сообщения сохранились без
дубликатов, обе sessions завершены, NodeId устойчив, writer пуст и порт освобождён.
Подробности — в [аппаратном отчёте](../../testing/messenger-b7-4-serial-2026-09-28.md).

Физический unplug/replug выполнен из устойчивого `Online`: разрыв перевёл supervisor
в `RetryWaiting`, неудачные открытия создали только последовательные generation с
backoff 1/2/4/8/15 секунд, а повторно подключённая нода вышла в `Online` в новой
session с тем же NodeId. Shutdown завершил session и durable barriers, pending ingest
равен нулю, порт освобождён. TCP не проверялся, поскольку endpoint не указан; этот
пункт был условным «по возможности» и не блокирует приёмку Serial-профиля.

Проверено: полный Release build без предупреждений; Core tests — 97/97, Desktop —
46/46, MeshCoreSharp regression suite — 92/92. **Этап B завершён 28.09.2026.**
