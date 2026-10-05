# D7.2 — автоматические повторы ЛС и история доставок по маршрутам

**Статус: P1–P6 реализованы и проверены 05.10.2026; P7–P8 ещё не реализованы.**
Пользователь разрешил реализацию по частям. [Stage D](stage-d.md), [D7](d7-manual-retry.md).
Основание: [исследование attempt/packet hash/ACK](../../library/protocol/private-retry-hashes.md).
Это явное расширение прежнего D7: пользователь теперь разрешает ограниченные
автоматические повторы внутри текущей отправки, не replay после reconnect/restart.

## Текущий checkpoint — P6

Реализованы явные timestamp/attempt в библиотечном SendTextAsync (полный ключ и
Contact), encoder базовых attempts 0…3 и Core adapter. Старые overloads по-прежнему
выполняют одну передачу с автоматически выбранным timestamp/attempt 0.

В Core добавлены immutable PrivateRetryPolicy/PrivateRepeatMode и внутренний
PrivateRetryPlan. Планировщик строит 3/5 шагов обоих режимов, разделяет локальный
AttemptNumber/сетевой WireMessageOrdinal/WireAttempt и требует новую reservation
для fallback. Сам планировщик не владеет сессией/БД и не выполняет TX/reset.

P4 передаёт immutable RetryPolicy через PrivateSendRequest и включает coordinator
в production Desktop DI. SendPrivateAsync возвращает Queued/Prepared после durable
принятия job; UI не ждёт MSG_SENT/всех ACK. Исходный flood выполняет до трёх
передач выбранного режима. P5 подключает known ×3 → owned reset/readback → flood ×2;
после reset создаётся новое сетевое сообщение при прежнем локальном MessageId.
Маршрут читается перед каждым TX, смена known-пути сохраняется в capture, а известный
путь в flood-фазе условно сбрасывается. Ошибка reset/readback/local commit останавливает
job без повторной mutation. Schema v5; P6 добавляет чтение истории и provenance; следующий **P7** — incoming dedup.
[Отчёт P5](../../testing/private-fallback-retries.md).

P2 добавил циклы, сетевые идентичности и неизменяемые снимки попыток; durable
PreparePrivateAttempt резервирует timestamp в одной транзакции с подготовкой.
Повтор операции идемпотентен, следующая попытка разрешена только после
Unconfirmed предыдущей. Счётчик общий для ЛС собственной ноды и переживает
перезапуск/очистку переписки. Настоящее время ПК, UTC offset и timezone хранятся
отдельно от wire timestamp. После startup активный цикл становится Unknown без TX.
Созданы таблицы истории доставок; чтение и обогащение маршрута реализованы в P6. [Отчёт P2](../../testing/private-delivery-storage.md).

Проверки P1: Release build 0 warnings/errors; Library 105/105, Core 282/282, Desktop 315/315.
Проверки P2: Release build 0 warnings/errors; Library 105/105, Core 303/303, Desktop 315/315.
Проверки P3: Release build 0 warnings/errors; Library 105/105, Core 312/312, Desktop 315/315.
Проверки P4: Release build 0 warnings/errors; Library 105/105, Core 330/330, Desktop 318/318.
Проверки P5: Release build 0 warnings/errors; Library 105/105, Core 345/345, Desktop 318/318.
Проверки P6: Release build 0 warnings/errors; Library 105/105, Core 354/354, Desktop 318/318.

Coordinator P4 держит FIFO по NodeId/session/generation/full contact key: максимум
32 job всего и 8 одному контакту, reservation до Prepare/transfer draft. Целый job
принадлежит исходному parent lease; каждый TX — новый child lease того же scope,
один BindOutgoing и один typed invocation. Ожидание ACK не держит immediate gate.
Любой durable Delivered пробуждает job и отменяет только текущий attempt waiter.
Disconnect/caller cancellation/persistence pause прекращают работу без replay.
История читает общий progress: Sending n/3 между попытками, Delivered имеет
приоритет над состоянием последней попытки. PC Sending time, фактический flood
flag и диагностический deadline сохраняются. [Отчёт P4](../../testing/private-delivery-coordinator.md).

P3 объединяет raw ACK, waiter completion и Delivered transition через один commit.
Все совпадения группируются по MessageId: одна группа подтверждает цикл, несколько
групп не подтверждают никого. При нескольких кандидатах одного сообщения попытки
не переписываются произвольно: аналитика сохраняет всех кандидатов. Итог цикла,
первый успех и добавочные evidence пишутся атомарно и идемпотентно; ReceivedUtc
хранится как фактическое время ПК, независимо от технического CompletedUtc.
ACK буферизуется по порядку writer до завершения регистрации всех Sending/MSG_SENT.
Библиотечный protocol fault, оставивший неполные collision metadata, не считается
однозначным успехом. Legacy доставка записывается с Unknown attribution без
вымышленного маршрута. [Отчёт P3](../../testing/private-delivery-ack-commit.md).
Новая TCP-эмуляция использует формулу ACK прошивки и явные одиночные вызовы,
а не готовый retry coordinator. [Отчёт P1](../../testing/private-retry-api.md).

## Требуемое поведение

Один исходящий MessageId/пузырёк и неизменяемый TransmissionText на весь цикл.
Цикл может включать несколько сетевых сообщений с разными timestamp. Каждая
реальная передача имеет отдельный durable SendAttempt; локальный AttemptNumber
сквозной, а протокольный WireAttempt отсчитывается внутри сетевого сообщения.

| Исходный маршрут | Передачи, включая первую | Timestamp / WireAttempt по умолчанию |
| --- | --- | --- |
| Неизвестный/flood | 3 flood | T1/0, T1/1, T1/2 |
| Direct (0 хопов) или известный путь | 3 по известному пути, reset, 2 flood | T1/0, T1/1, T1/2 → reset → T2/0, T2/1; T2 > T1 |

Таким образом, при known → flood создаётся новое **сетевое** сообщение, но нового
локального пузырька нет. Это заменяет прежний проект с attempt 0…4 при одном timestamp.
Внутри каждой фазы стандартный повтор меняет attempt; граница фаз всегда резервирует
новый timestamp и сбрасывает attempt. Первая передача любой сетевой идентичности — 0.

Политика фиксируется при принятии исходного сообщения. Конкретные 3 или 5 передач
выбираются по маршруту ноды при начале job, перед первым TX: за время очереди
маршрут мог обновиться. Затем бюджет не меняется. На каждом шаге проверяются
сессия, наличие/однозначность контакта и актуальный маршрут ноды.
Полученный ACK любой попытки прекращает будущие передачи этого сообщения.
Во время ожидания показывается «Отправка (попытка n/N)», затем Delivered либо
после исчерпания бюджета «Ошибка». Поздний ACK может перевести итог в Delivered.
Смена чата не прекращает цикл. Выход/обрыв/смена ноды прекращают цикл без replay.
Публичные действия и обычная библиотечная отправка одним вызовом сохраняются.

## Архитектурная проверка уточнения

Противоречия с протоколом и разделением ответственности нет: timestamp/attempt уже
являются полями Companion-команды. Устройство управляет путём; Core управляет
переходами между фазами, сетевыми идентичностями и локальным MessageId.

- Смена timestamp обычно даёт новый ExpectedAck. Детерминированное совпадение attempt
  0/4 исчезает, поскольку в этом плане attempt не превышает 2. Случайные коллизии
  32-битного тега по-прежнему возможны: защиту matching сохранять.
- Расширенный attempt > 3 не требуется. Лимит ЛС остаётся **160 UTF-8 байт**, без
  дополнительного выбора 158 байт или поддержки расширения firmware.
- Нельзя хранить один неизменный wire timestamp у всего цикла. Он относится к сетевому
  сообщению/попытке; ACK от T1 остаётся действительным и после начала T2.
- Новый timestamp меняет идентичность сообщения на стороне адресата. Если T1 дошло,
  но ACK потерялся, T2 может появиться там вторым сообщением. Аналогично ведёт себя
  режим нового timestamp при каждом повторе. Это выбранный компромисс доставки,
  а не гарантия exactly-once; ни протокол, ни наш sender не могут его устранить.

Наш приёмник объединяет только повторы одной сетевой идентичности. Он не объединяет
T1 и T2 по одинаковому тексту/близости времени: иначе потеряются настоящие одинаковые
сообщения пользователя. Результат дедупликации стороннего приложения проверить
аппаратно; прежний эксперимент публичного чата не доказывает поведение всех ЛС.

## API выбора режима повторов

PrivateRetryPolicy/PrivateRepeatMode реализованы в P1; передача политики через
PrivateSendRequest/SendPrivateAsync и flood-исполнение подключены в P4. Known/fallback подключены в P5. Для обычного UI
будет использоваться стандартная политика; новую настройку UI в этот этап не добавлять.

| PrivateRepeatMode | Переход следующей передачи | Следствие у адресата |
| --- | --- | --- |
| SameTimestampIncrementAttempt | Сохранить текущий WireTimestamp; WireAttempt + 1 | Повтор доставки того же сетевого сообщения; dedup возможен по sender/timestamp/text |
| NewTimestampResetAttempt | Зарезервировать новый WireTimestamp; WireAttempt = 0 | Новое сетевое сообщение, возможен дополнительный пузырёк |

PrivateRetryPolicy.RetryMode выбирает режим **внутри фаз**. Числа передач фиксированы
стандартной политикой: исходный flood 3, known 3 и fallback flood 2. Переход known →
fallback — отдельное правило политики: всегда NewTimestampResetAttempt, независимо
от RetryMode. Таким образом API не смешивает смену маршрута и способ сетевого повтора.

- Default: RetryMode = SameTimestampIncrementAttempt; расписание указано выше.
- NewTimestampResetAttempt: исходный flood T1/0, T2/0, T3/0; known T1/0, T2/0,
  T3/0 → reset → T4/0, T5/0. Общие бюджеты и один локальный пузырёк сохраняются.
- Политику копировать/валидировать при принятии job. Изменение настроек позднее
  не меняет начатый цикл. Не давать бесконечных повторов или неявного переполнения
  attempt; расширенные attempts вне этого этапа.

Техническое приложение «как новое сетевое сообщение» не равно действию «Отправить
как новое» в публичном меню: последнее намеренно создаёт новый локальный MessageId.
Новый явный вызов SendPrivateAsync также создаёт новый MessageId. Режим повтора
действует только внутри принятого цикла, не перечитывает draft и не меняет текст.

MeshCoreSharp получает отдельный typed overload SendTextAsync с явными timestamp и
attempt (либо value object этих двух полей). Он делает **один TX**, не знает RetryMode,
фаз, БД или маршрута. Существующий overload сохраняет attempt 0/авто timestamp.
Оба варианта поддерживают CancellationToken; явный timestamp продвигает библиотечный
timestamp floor, чтобы следующий обычный вызов не переиспользовал его.
Для этого этапа encoder поддерживает базовые attempt 0…3; неподдерживаемые расширенные
значения отклоняет до TX. Планировщик обоих режимов выдаёт только 0…2.

## Что уже есть и что мешает простой реализации циклом

| Существующий компонент | Как использовать / что изменить |
| --- | --- |
| MessageService | Сейчас Prepare → один TX → отдельный ACK observer. Оставить фасадом, вынести управление циклом ЛС в Core PrivateDeliveryCoordinator |
| SessionCommandGateway/Lease | Уже владеют node/session/generation и quiesce. Зарегистрировать полный цикл, задержки и все попытки до возврата из первой отправки |
| ConversationOperationGuard | BeginSend допускает несколько отправок одного контакта, exclusive reset их запрещает. Нужны управление очередью циклов по контакту и owned reset внутри цикла |
| ContactRouteService | Рабочий reset/readback; текущий public ResetAsync нельзя вызвать из отправки: конфликт с её же BeginSend |
| AckTracker | Поддерживает параллельные ожидания и ограничение 8 слотов; duplicate active tags отклоняет. Его защиту не отключать |
| OutgoingAttemptWriteTracker | Durable очередь, pause на ошибке, ранние/поздние ACK. Расширить единым commit доставки сообщения/analytics |
| SqliteOutgoingMessageStore | ACK сейчас требует ровно одно совпадение, даже завершённые attempts участвуют в collision check. Нужна группировка по логическому сообщению |
| SqliteLocalHistoryReader | Сейчас показывает последнюю попытку. Нужен итог сообщения поверх attempts, иначе поздний ACK старой попытки не исправит пузырёк |
| Contacts/DirectoryService/ContactRouteRefreshQueue | Хранят только текущий путь и обновляют его по PATH_UPDATED/readback. Истории маршрутов нет |
| DatabaseMigrator | P2: схема v5, backup до миграции; legacy попытки сохраняются без вымышленных снимков |
| SqliteIncomingMessageStore | Идемпотентность только по EventId. Разные попытки ЛС могут стать разными входящими записями |

Companion-библиотека остаётся одним MeshCoreSharp.dll, без БД/авторетрай-политики.
Никаких radio retries в dispatcher/transport. Каждая попытка — один обычный
subscribe-before-send exchange; RX/push продолжают работать независимо.

## ACK и безопасность выбора попытки

ExpectedAck из MSG_SENT сохранять отдельно для каждой передачи, включая смену
timestamp. Не заменять теги T1 тегами T2: поздний ACK первой фазы подтверждает
тот же MessageId, прекращает дальнейшие TX и сохраняет маршрут именно его попытки.
Если ACK пришёл во время подготовки fallback, проверить итог перед reset и TX.

При случайном совпадении тегов одной session можно подтвердить MessageId, если
все совпавшие attempts принадлежат только ему; конкретную успешную попытку и маршрут
не угадывать. Между разными MessageId такой тег считать collision, не выбирать
самый новый. Не держать два активных библиотечных Delivery-waiter одного тега:
последовательные попытки завершают прежний waiter, а поздние ACK поступают в Core
по session events. Не отменять весь цикл при отмене лишь ожидания одной попытки.

## Владение циклом, очереди и маршрут

PrivateDeliveryCoordinator в Core принимает захваченный текст/контакт текущей
session. Он владеет job сообщения, отменой, фазами, номером попытки и сроками.
MessageService продолжает валидировать capture и транзакционно передавать текст
из draft в outgoing store. UI не управляет timers/ResetPathAsync.

Сейчас SessionCommandLease может BindOutgoing только один attempt и допускает
один send invocation. Это ограничение сохранить: lease всего цикла регистрирует
workflow, а каждая передача получает отдельный owned attempt lease того же
захваченного scope/target. Добавить внутреннее создание дочернего lease с проверкой
Owner/generation и admission; не получать для следующей попытки случайно новую
session через текущий gateway. Родитель владеет дочерними работами и ждёт их cleanup.

Для одного NodeId + полного ключа контакта циклы выполняются последовательно.
Следующее явно отправленное сообщение можно Prepared-сохранить/поставить в очередь,
не очищая чужую ревизию draft. UI возвращает управление после durable принятия job,
а не ждёт всех ACK: фасад получает явный результат «принято в очередь» отдельно от
MSG_SENT. Это уточнение текущего PrivateSendOutcome/API покрыть тестами. Очередь
принадлежит только текущей session, ограничена по размеру; при quiesce не начинает
новый TX. Prepared после restart не отправляется. Это не background outbox.

Разные контакты могут ждать ACK одновременно. Global immediate gate занимает
только одну команду; application send gate — только Prepare/Sending/MSG_SENT,
не весь тайм-аут. Не блокировать канал/другие контакты на 3–5 radio ожиданий.
Сохранить окно AckTracker для кольцевой firmware ACK table.

Сброс после третьего неуспешного ожидания выполняет владелец цикла, используя
существующие typed lease ResetPathAsync/GetContactAsync и DirectoryService write.
Выделить общий внутренний owned-reset helper; ручной ContactRouteService по-прежнему
берёт external exclusive guard, автоматический helper не пытается получить его
повторно. Полный цикл удерживает guard от clear/history/contact mutations.

Перед каждой передачей снять текущий контакт через typed GetContactAsync и сохранить
snapshot. Маршрутом физически управляет нода; CMD_SEND_TXT_MSG не принимает path.
Не копировать OutPath в Contacts как средство принудительного восстановления
старого пути. В known-фазе использовать актуальный известный путь и записывать его
изменения. В flood-фазе при обнаруженном известном пути условно reset повторить
перед TX, если прежний reset/readback успешен; не повторять mutation из-за ошибки БД.
После MSG_SENT сверить фактический flood flag и сохранить его, отличая от намерения.
Не приписывать known route, если нода фактически послала flood или наоборот.

Узкая гонка: PUSH/прошивка может изменить путь между GetContact и SEND_TXT_MSG;
MSG_SENT сообщает flood/non-flood, но не полный использованный path. Snapshot
настроенного маршрута — наблюдение, а не доказанная трасса эфира. Если для
исследования нужен буквально неизменный путь на все первые три TX или строго
точный путь каждого пакета, потребуется отдельное подтверждение поддержки firmware/
радиолог; этого обычный API не гарантирует. Эта оговорка должна быть в analytics.

Проверять durable Delivered непосредственно перед prepare/reset/send. ACK между
проверкой и уже начатым TX может допустить одну передачу, которую нельзя отозвать;
после подтверждения никаких следующих. Не начинать автоматический повтор при
Unknown/потере transport response, смене session, persistence pause, неоднозначном
контакте, явном ERROR или ExpectedAck=0. Retry-триггер — достоверный ACK timeout.
Тайм-аут берётся из MSG_SENT с существующими bounds/margin; delay отменяемый,
тестируется TimeProvider. Ни RX loop, ни global command gate на нём не блокируются.

P6: IContactDeliveryHistoryReader возвращает scoped страницы успехов и все
ACK candidates с PC times/offset/zone. После commit нового evidence session-owned
queue снимает отдельный ContactReadbackAfterAcknowledgement snapshot; ошибка чтения
оставляет null, SQL recovery не выполняет radio replay. ConfiguredBeforeSend и
readback не смешиваются. Снимки переживают clear/contact removal. Schema v5 без
миграции. [Отчёт P6](../../testing/private-delivery-history-reader.md).

## Данные и миграция

Ниже — исходные требования к данным; схема v5 реализована в P2, общий ACK commit
и запись успешной доставки — в P3. Конкретные SQL-имена описаны в отчётах частей.

### Цикл и attempts

Таблица/record PrivateDeliveryCycles: MessageId, NodeId, SessionId, ContactPublicKey,
InitialRouteKind, PlannedAttemptCount, CurrentPhase, TotalOutcome, ConfirmedUtc,
PolicyVersion, RetryMode. Один цикл/сообщение; OriginalText/TransmissionText не
перезаписываются. Одного поля WireTimestamp здесь недостаточно.

Сетевые идентичности представить PrivateWireMessages (название проектное): Id,
MessageId, WireMessageOrdinal, Phase, WireTimestamp. Default known → flood создаёт
две такие записи; режим NewTimestampResetAttempt — до пяти. Каждая имеет свои
attempt 0…2. WireMessageOrdinal отличается от session Generation.
Исходные время/положение Messages в истории не меняются при T1 → T2: новый timestamp
хранится в сетевой идентичности/attempt, не перемещает исходный пузырёк.

SendAttempts расширить WireMessageId, WireAttempt, route snapshot, phase, deadline
и metadata состояния ожидания. AttemptNumber — сквозной локальный ordinal 1…N,
WireAttempt — протокольное значение 0…2. Для known default локальные attempts 4/5
ссылаются на T2, а не на перезаписанное T1. Timestamp и tag каждой попытки неизменяемы.
WireTimestamp/WireAttempt/route capture фиксируются до Sending/TX. У legacy nullable
поля не заполнять выдуманной историей; старые статусы сохранять.

Новый timestamp резервировать транзакционно при начале первой сетевой передачи
или при NewTimestampResetAttempt, а не заранее для ещё ожидающего job. Allocator
для собственной ноды: max(UTC Unix seconds, сохранённый floor + 1); T2 > T1 даже
в ту же секунду или при переводе часов назад. Общий durable floor учитывать для
всех контактов ноды, сохранять после clear-history/restart; исчерпание UInt32
не допускает wrap. Это уменьшает искусственные коллизии одинаковых сообщений
разным адресатам; случайные 32-битные коллизии остаются. Новый timestamp обязательно
сохраняется до TX. Failed prepare/reserve = 0 TX; Capture всё ещё проверяется.

Полный маршрут: encoded OutPathLength, фактически используемые Path bytes (без
padding 64-байтного поля), HashSize, HopCount, RouteKind (flood/direct/path/unknown),
ObservedUtc, provenance. OutPathLength=0xFF означает unknown/flood; остальные значения
декодировать общим helper, а не считать сырое значение количеством хопов.
Hash репитера в path — усечённый идентификатор, не полный публичный ключ.

### История успешных доставок контакта

Отдельная append-only таблица ContactDeliveryHistory, identity: собственный NodeId +
полный ContactPublicKey, не имя/IP/6-байтный prefix. Поля:

- DeliveryId (стабильный id/idempotency key), nullable MessageId/SessionId references,
  WireMessageId/ordinal, WireTimestamp, WireAttempt, локальный AttemptNumber, Phase,
  AckTag, outcome, подтверждение обычное/позднее.
- SentUtc и AckReceivedUtc **по часам ПК**; offset и timezone id для анализа времени
  суток при смене часового пояса. Это не время RTC ноды и не синтетический WireTimestamp.
- ConfiguredRoute snapshot перед передачей, ModeReportedByMsgSent, RTT от ноды,
  локальная monotonic duration отдельно (wall clock может переводиться назад).
- Attribution (single-attempt / multiple-candidates / unknown), candidate attempts/routes,
  first evidence time; learned route/readback после ACK отдельным snapshot/provenance.

При уникальном теге различаются успех known/T1 и fallback/T2, даже если поздний ACK
T1 пришёл после начала flood. При случайной коллизии SentUtc одной точной передачи
неизвестно: сохранять список candidate SentUtc/маршрутов, а не выбирать последнюю.
AckReceivedUtc точно наблюдаемое.
При уникальной попытке маршрут до TX всё равно labelled configured snapshot;
при flood learned route — найденный путь для будущих отправок, не доказательство,
что этот path был единственным успешным для текущей передачи. Отсутствие/ошибка
readback не уничтожает подтверждённую доставку, learned route остаётся null.

Сохранять delivery evidence и успех атомарно с итогом Delivered (в том же writer
transaction); повтор ACK/observer/повтор SQLite write не создаёт повтор успеха.
У одного MessageId один delivery result, возможны добавочные свидетельства ACK
других attempts/сетевых сообщений, но не повторное увеличение числа доставленных
локальных сообщений. Первый подтверждённый успех сохраняется; последующие ACK —
отдельные evidence с их маршрутами/timestamp. Они не доказывают отсутствие дубликата
у адресата и не заменяют маршрут первого успеха маршрутом последней передачи.
Read API — постраничный по (NodeId, ContactPublicKey, AckReceivedUtc, DeliveryId),
индексы под него. Contacts хранит только текущую конфигурацию, массив истории туда
не добавлять. UI аналитики/графики пока не входит.

Предлагаемая retention: локальная очистка переписки удаляет текст и attempts,
но не route/time delivery records без текста. Correlation references nullable
с ON DELETE SET NULL либо независимые audit ids; не CASCADE от Messages/Contacts.
Удаление собственной ноды может удалить всю её историю. Этот выбор отметить в
контракте clear-history и тестах; не сохранять текст/секреты в аналитической таблице.

Для будущего «какой путь лучше» нужны и неуспешные попытки/число передач, а не только
успехи: route snapshots сохраняются у всех attempts. Отдельная долговечная история
неудач/проценты после очистки текста — последующее расширение, не выводить ошибочный
success rate только из списка успешных доставок.

## ACK, итог сообщения и входящие повторы

В существующем session event pipeline ACK уже записывается через durable tracker;
его использовать как единый authoritative delivery evidence, не создавать второй
reader транспорта. Subscribe before send и early-ACK buffer сохраняются.

ConfirmAcknowledgementAsync: найти все совпадения NodeId/SessionId/tag (включая
завершённые attempts); сгруппировать MessageId. Ровно одна группа → Delivered
сообщения с candidate evidence; несколько → ambiguous, ни одно чужое сообщение
не подтверждать. Не переписывать отдельную попытку Delivered без знания её номера.
RecordDeliveryAsync/waiter completion может сигналить coordinator, но пишет через
тот же idempotent commit. Store result/notification должен позволять остановить
job по MessageId; delivery check перед новым шагом обязателен.
Library Confirmed само по себе не обходит durable collision check: при совпадении
с тегом другого сообщения его waiter не должен самовольно записать Delivered.
Успех/остановка цикла основаны на однозначно подтверждённой группе в Core.

Текущую проверку ReceivedUtc >= StartedUtc нельзя использовать как доказательство
порядка при переводе часов ПК назад. Для admission раннего ACK использовать порядок
session events/монотонную отметку, сохраняя истинное wall-clock время наблюдения
отдельно. Старые CHECK constraints времён статусов проверить при миграции; техническое
CompletedUtc и фактическое AckReceivedUtc аналитики не обязаны совпадать.

History DTO/presentation читают TotalOutcome/ConfirmedUtc плюс текущий прогресс.
Delivered приоритетнее состояния последней попытки, не исчезает при позднем
Unconfirmed/cleanup этой попытки. Пауза между передачами не показывает окончательную
красную «Ошибка». После исчерпания budget — итог Unconfirmed, поздний ACK допустим.
Если невозможно подтвердить связь ACK после reconnect (tag не несёт SessionId),
не расширять matching на все сессии наугад; такая поддержка требует отдельного
безопасного audit всех persisted tags. Автоповтора после reconnect всё равно нет.

Входящие ЛС: в транзакции ingest dedup по NodeId + resolved full sender identity +
type + sender timestamp + exact text/extra для plain. SessionId/attempt/path/SNR
не считать идентичностью смыслового сообщения. При повторе не увеличивать unread,
не создавать пузырёк; новые path/SNR можно хранить как observation. Для unknown/
ambiguous sender не объединять по шестибайтному prefix без однозначного разрешения.
EventId идемпотентность остаётся для технического replay одного события.
Текст одинаковый с новым timestamp остаётся отдельным сообщением.
В частности T1/T2 одного sender fallback могут дать два входящих пузырька: общего
MessageId отправителя на wire нет. Обещать receiver-side exactly-once этому API нельзя.

## Порядок реализации — небольшие отдельные шаги

| Шаг | Scope | Проверка перед переходом |
| --- | --- | --- |
| P1 — выполнен | Typed библиотечный send с явными timestamp/attempt; Core contracts RetryMode/Policy; прежний overload одноразовый | Encoder 0–3 и reject расширения, 160/161 bytes, явный timestamp floor, два режима планировщика, MSG_SENT-before-ACK; [отчёт](../../testing/private-retry-api.md) |
| P2 — выполнен | Миграция cycle/wire-message/attempt/route/history, PreparePrivateAttempt, timestamp allocator | Upgrade v4, backup, legacy, prepare failure = 0 TX, устойчивый allocator, неизменность T1/T2 |
| P3 — выполнен | Общий commit ACK/message outcome/history; группировка по MessageId и защита collisions | ACK разных фаз/старого timestamp, early/late, idempotent commit, persistence pause/retry без TX |
| P4 — выполнен | Session-owned coordinator, per-contact queue, три flood TX, оба RetryMode, progress и отмена | Virtual time, stop on any ACK, timeout ×3, два сообщения одному контакту, UI один пузырёк, no replay |
| P5 — выполнен | Owned reset/readback, known ×3 + flood ×2, новое сетевое сообщение после reset | Reset после третьего timeout, T1/0…2 → T2/0…1, два режима, route mutation guard, late ACK T1 |
| P6 — выполнен | Route provenance/learned snapshot, аналитический read API, retention | Single/ambiguous route success, PC time/zone/clock change, clear/contact remove |
| P7 | Ingress dedup и итоговые UI projections, прежний byte counter 160 | Same timestamp → одно входящее/unread, new timestamp → второе, один исходящий пузырёк |
| P8 | Совместная loopback/native приёмка и аппаратные эксперименты пользователя | Полная матрица ниже, обновлённые docs, user confirmation |

P2 уже создаёт фундамент history, P3 атомарно пишет успех, P6 завершает чтение,
route enrichment/retention; не добавлять запись аналитики отдельным ненадёжным callback.
Новый UI-компонент не нужен: metadata под пузырьком и текущий route header уже есть.
Лимит остаётся 160, расширенный attempt не входит. P1–P6 завершены; P7–P8 ещё не реализованы.

## Обязательная матрица

- Flood: timeout три раза → ровно 3 TX; ACK 1/2/3 → остановка на соответствующей попытке.
- Known default: timeout пять раз → ровно 5 TX, reset перед четвёртой,
  T1/0, T1/1, T1/2, T2/0, T2/1; T2 > T1, attempt 3/4 не отправляется.
- NewTimestampResetAttempt: отдельный timestamp/attempt 0 на каждый TX, те же 3/5
  передач и route phases. Выбор режима фиксируется при enqueue; timestamp резервируется
  перед началом соответствующей сетевой передачи, не переиспользуется после restart.
- Поздний ACK T1/0 во время T1/2; ACK T1 после начала T2; окончательная ошибка → поздний Delivered;
  old tag другого сообщения не подтверждает новое, ambiguity не выдаёт выдуманный маршрут.
- Два сообщения одному контакту: очередь без смешения ACK/route reset; разные контакты
  не блокируются на все ожидания; кольцевой ACK table capacity и collisions сохраняются.
  Маршрут изменился во время очереди: initial budget выбирается при начале job,
  policy snapshot остаётся выбранным при enqueue.
- Смена route между read/send, неожиданный MSG_SENT mode, PATH_UPDATED во время timeout,
  failed reset/readback/SQLite write; не повторять mutation/TX ради сохранения.
- Disconnect/shutdown/persistence pause до/после Prepare/Sending/MSG_SENT; restart = 0 TX;
  clear-history и ручной reset не вмешиваются в цикл. Старые owned writes завершаются.
- 160/161 UTF-8 байт, emoji, обычный firmware attempt; capture draft изменился
  после клика, повтор не забирает новый текст. Два публичных действия без регрессии.
- Один ACK/дубликаты push/observer → одна запись успеха; route/time сохранены после
  локальной очистки; неоднозначные route/time candidates сохранены честно.
- Приём: T1 с разными attempts объединяется; T2 с тем же текстом — отдельная запись.
  Эмулятор «текст дошёл, ACK потерян» воспроизводит возможный дубликат после fallback;
  фильтр одинакового текста по временному окну не добавлять.
- Аппаратно: пять payload/hash различаются в контрольном наборе; первые три имеют T1,
  последние две — T2. Проверить ACK tags, duplicate/retry поведение штатного приёмника,
  поздний ACK первой фазы; не обещать dedup стороннего приложения до эксперимента.
