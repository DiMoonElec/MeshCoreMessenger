# D3 — durable исходящие и startup recovery

[Stage D](stage-d.md) · **Статус: реализован, автоматически проверен; ручная приёмка ожидается.**

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

## Реализация 03.10.2026

- `LocalStorage.OutgoingMessages` предоставляет `PrepareAsync`, `TransitionAsync`,
  `GetAttemptsAsync` и отдельный `MessageCommitted`. В production composer отправка
  пока выключена. Client/session/TX/ACK observer к store не подключаются.
- `OperationId` совпадает с `Messages.Id`. Сообщение, первая попытка и timestamp
  переписки записываются одной транзакцией. Повтор того же ID возвращает сохранённую
  попытку; другой текст, node/session/conversation/recipient/binding отклоняются.
  Повтор уже сохранённой операции допустим после закрытия session/binding и не
  начинает новую попытку. Черновик этот API не очищает.
- Prepare проверяет активную session той же ноды, разрешённую conversation, полный
  ключ контакта либо fingerprint канала. Контакт должен присутствовать, быть Chat,
  иметь однозначный wire prefix. Для канала проверяются binding ID/slot/generation,
  node/channel ownership и отсутствие UnboundUtc. D4 повторит проверку перед wire.
- `Messages.Text` хранит оригинал, `TransmissionText` — захваченный результат
  text processor. Текущая заглушка возвращает одинаковые строки; тест использует
  разные, чтобы доказать независимость хранения. Секретов канала нет.
- `Sending=6` добавлен без изменения 0–5. Переход — CAS конкретного attempt ID с
  проверкой node/message/session; terminal состояния неизменяемы. Допускаются
  Prepared → Sending/Failed, Sending → Accepted/Failed/Unknown, а после Accepted
  с Expected — Delivered/Unconfirmed/Unknown. Delivered нельзя сохранить раньше
  Accepted; late callback с прежним состоянием возвращает false.
- `AckExpectation`: LegacyUnknown=0, NotExpected=1, Expected=2. Accepted+Expected
  отображается AwaitingAck; Accepted+NotExpected — AcceptedByNode и завершён.
  Для Expected сохраняется uint32 ACK tag; канал не может ожидать ACK.
  Immutable snapshots содержат attempt number, timestamps, ACK metadata и ошибку.
- Миграция v3 добавляет TransmissionText и AckExpectation, пересобирает только
  SendAttempts для CHECK 0–6, сохраняет прежние значения, FK, unique constraint и
  индекс. Старые миграции не изменены. До открытия writer/upgrading выполняется
  проверенный SQLite backup в BackupsDirectory; он включает committed WAL.
  Копия переключается в DELETE journal mode и не требует WAL sidecars.
  Неполная копия удаляется; отказ backup запрещает upgrade. Новая БД backup не требует.
- До возврата LocalStorage startup recovery атомарно переводит Sending и
  Accepted+Expected в Unknown. Legacy Accepted личного/неизвестного адресата также
  становится Unknown: нельзя вывести NotExpected из отсутствия сохранённого ACK.
  Legacy Accepted канала получает NotExpected. Prepared и terminal states сохраняются;
  повторный startup идемпотентен. Restore восстанавливает состояния в staging copy
  до замены активной БД. Ни один из этих путей не вызывает TX.
- History pages используют bounded LEFT JOIN последней попытки по существующему
  unique index (MessageId,AttemptNumber), без запроса на каждый bubble. DTO содержит
  LatestAttempt; общий desktop formatter отображает статус под пузырьком, сохраняя
  copy оригинала. Retry не активируется. Исходящие не увеличивают unread.
- Уведомление outgoing публикуется после commit; duplicate prepare, CAS без изменения
  и failed write не публикуют его. Это invalidation с node/conversation/message ID,
  а не авторитетный snapshot: consumer перечитывает проекцию. Исключение подписчика
  не отменяет commit и не мешает другим. Live wiring consumer к отправке — D5/D6;
  incoming notification и read watermark в D3 не меняются. Root VM не расширена.

## Проверки

Временная настоящая SQLite: rollback при отказе вставки попытки после Messages,
идемпотентность и mismatch, wrong ownership/recipient/retired binding, CAS и terminal
ordering, отказ записи перехода без notification, concurrent CAS, cancellation,
reopen каждого состояния и NotExpected, bounded before/after/around projection,
upgrade настоящей v2 SendAttempts с legacy Accepted, FK/index, migration rollback,
pre-migration backup с WAL и отказ backup без изменения версии. Delivered в тестах
достигается исключительно через Prepare/Sending/Accepted/Delivered API.

Debug/Release solution builds: 0 warnings/errors. Regression suites в обеих
конфигурациях: библиотека 96/96, Core 159/159, Desktop 265/265. Desktop включает
100 000 сообщений/1994 history pages, bounded DTO peak 512; Release SQL page
p50/p95 0,30/0,39 ms в текущем прогоне. Restore recovery проверен отдельно.
Первый Release Desktop runner не завершился и был остановлен; диагностический
повтор с `-longRunning 20` прошёл за 17,7 s, Debug — за 16,7 s. Причина первого
зависания не установлена, его не учитываем как успешный прогон.
`git diff --check` и относительные ссылки проверены.

Ручная приёмка пользователем D3 пока не проводилась. Эфир не требуется; UI preview
D1 остаётся доступен по `dotnet run --project tools/MeshCoreMessenger.SendUiPreview`.
