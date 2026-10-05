# Хэши радиопакетов в сведениях о сообщении — исследование

Дата: 05.10.2026. Статус: отложенная идея; реализация сейчас не планируется. Исследована текущая main
MeshCore и исходники приложения после P5; возможности аппаратной прошивки нужно
проверять отдельно. [Текущее состояние](current-state.md),
[исследование ЛС](../../library/protocol/private-retry-hashes.md),
[канальный эксперимент](../../testing/channel-repeat-hardware-dedup.md).

## Доступность данных

По [Packet.cpp](https://github.com/meshcore-dev/MeshCore/blob/main/src/Packet.cpp#L37-L45)
хэш текстового радиопакета — усечённый SHA-256 от payload type и wire payload.
Размер — 8 байт, 16 hex-символов, по
[MeshCore.h](https://github.com/meshcore-dev/MeshCore/blob/main/src/MeshCore.h#L6).
Path, route type и transport codes текстового пакета не включаются; TRACE имеет
отдельное правило. ACK-тег 4 байта и fingerprint канала не являются packet hash.
Нельзя хэшировать Companion frame либо открытый текст вместо радио payload.

[MyMesh.cpp](https://github.com/meshcore-dev/MeshCore/blob/main/examples/companion_radio/MyMesh.cpp#L266-L277)
отдаёт RX raw в push 0x88: type, signed SNR/4, signed RSSI, radio bytes; нужен
подключённый интерфейс и достаточная длина frame. MSG_SENT и очередь сообщений
packet hash не содержат. TX log hook в штатном Companion не переопределён:
[MyMesh.h](https://github.com/meshcore-dev/MeshCore/blob/main/examples/companion_radio/MyMesh.h),
[Dispatcher.h](https://github.com/meshcore-dev/MeshCore/blob/main/src/Dispatcher.h#L145-L149).
RX log вызывается до разбора/дедупликации:
[Dispatcher.cpp](https://github.com/meshcore-dev/MeshCore/blob/main/src/Dispatcher.cpp#L175-L219).
Поэтому в нём бывают копии, чужой трафик и повреждённые пакеты без пузырька.

| Сценарий | Возможность без изменения Companion |
| --- | --- |
| Любой валидный RX raw | Точный хэш вычисляется без расшифровки. Принадлежность пузырьку отдельно не доказана |
| Канальный TX | Вычислим при точном захвате секрета, имени отправителя, timestamp и wire text; физический TX этим не подтверждается |
| Канальный RX | Привязка через проверку MAC/расшифровку raw с ключом канала и сверку сообщения; копии одного payload объединяются по хэшу |
| Личный TX | Не вычислим из имеющихся application DTO: отсутствует общий криптографический секрет |
| Личный RX | Хэш raw вычислим, но достоверная связь с открытым сообщением отсутствует в стандартном queue API |
| Offline backlog / старые сообщения | Raw наблюдения могли отсутствовать; не заполнять выдуманные хэши |

Канальный encoder должен повторять
[формирование сообщения](https://github.com/meshcore-dev/MeshCore/blob/main/src/helpers/BaseChatMesh.cpp#L442-L459)
и [group payload](https://github.com/meshcore-dev/MeshCore/blob/main/src/Mesh.cpp#L499-L515).
Ключ Companion 16 байт дополняется нулями до 32 для HMAC, channel hash берётся
из SHA-256 исходного 16-байтного ключа; AES использует первые 16 байт.
[Шифрование/MAC](https://github.com/meshcore-dev/MeshCore/blob/main/src/Utils.cpp#L78-L132)
детерминированы. Нужны точные UTF-8 bytes, flags и padding; реконструкция очереди
не гарантирует точность для произвольных сторонних форматов/усечённого текста.
Реконструированный хэш помечать отдельно, наблюдённый raw считать авторитетным.

Для ЛС plaintext включает timestamp/attempt/text, wire payload — адресные
префиксы/MAC/ciphertext. Изменение attempt обычно меняет packet hash.
[Shared secret](https://github.com/meshcore-dev/MeshCore/blob/main/src/Identity.cpp#L142-L144)
требует приватного ключа ноды. Приложение его не получает; автоматический экспорт
приватного ключа ради диагностики не предлагается. Два пригодных решения:
согласованное расширение Companion, отдающее hash с TX result и входящим queue item,
или отдельный явно выбранный режим локальной криптографии с получением key material.
Последний вариант существенно меняет модель работы с секретами.

## Состояние проекта и точки расширения

- MeshCoreSharp знает PacketType.LogData, но CompanionPacketDecoder возвращает
  RawCompanionPacket. Нужны typed LogDataPacket/RadioPacketObservation и безопасный
  radio parser/hash calculator внутри существующей MeshCoreSharp.dll.
- CompanionSession уже копирует RawFrame и OccurredUtc. ReceiveCoordinator
  обрабатывает ACK/PATH_UPDATED/MessageReceived, но не сохраняет 0x88.
  Обрабатывать один PushPacketReceived, избегая повторной записи того же frame
  из PacketReceived/UnhandledPacketReceived.
- SQLite v5: SendAttempts и PrivateWireMessages уже задают точные попытки/T1/T2,
  но packet hash отсутствует. Входящий StoreAsync уникален по EventId; смысловой
  incoming dedup P7 ещё не выполнен. Messages.WirePayload сейчас не содержит
  радиопакет и не заменяет коллекцию наблюдений.
- Channel fingerprint сохраняется, секрет не хранится в БД. Захват для расчёта
  брать через typed session API, проверить fingerprint/binding, секрет использовать
  временно. Полученные digest/payload сохранять без ключа. Имя отправителя
  фиксировать при каждой TX: сегодняшнее имя не годится для старого пакета.
- History DTO читает лишь LatestAttempt. HasDetails зависит от presentation
  отправки, DetailsText — синхронный текст; MessageView открывает отдельный
  MessageActionDialog. Полную историю попыток/наблюдений читать новым details API.

## Предлагаемая модель хранения

Новые таблицы — проект, не существующий код. Следующая последовательная миграция
после актуальной схемы на момент реализации; не привязывать заранее к номеру v6.

**PacketObservations**: Id/EventId, NodeId, SessionId, event sequence, packet hash
BLOB(8), event kind/source, OccurredUtc, PcUtcOffsetMinutes, PcTimeZoneId,
payload type/version, optional raw/payload, path descriptor/bytes, RSSI/SNR.
Виды событий различают calculated/prepared, accepted by Companion, observed RX
и будущий firmware TX. Расчёт или принятие команды не обозначать фактом эфира.
Неполный/неподдерживаемый raw не выдаёт «точный» hash.

**MessagePacketLinks**: MessageId, ObservationId, nullable SendAttemptId,
nullable IncomingReceptionId, association kind/evidence. Допускается наблюдение
без связи; ambiguous candidates не выдаются за доказанную связь. FK и проверки
обеспечивают node ownership. Хэш не является глобальным уникальным MessageId:
коллизии и старые входящие дубли возможны.

**IncomingReceptions**: EventId, MessageId, session, время ПК и поля приёма.
При смысловом dedup P7 повтор создаёт reception/observations у прежнего MessageId,
а не увеличивает unread и не теряет новый packet hash. T1/T2 с новым timestamp
обычно остаются двумя входящими сообщениями: общего sender MessageId в wire нет.

Несколько записей с одним hash обязательны: повтор доставки канала может иметь
прежний hash, но новое время; прямая и ретранслированные копии также сохраняются
раздельно. UI группирует по hash и показывает события внутри группы. Несколько
hash у одного сообщения — обычная связь один-ко-многим, привязанная к attempts.
Не делать UNIQUE(MessageId, Hash) на таблице событий.

Время снимать при поступлении события/вызове отправки, до фонового writer.
Хранить реальный UTC часов ПК плюс offset/timezone на этот момент; не подменять
wire timestamp или временем SQL commit. Для ordering/replay добавить sequence
и устойчивый EventId, поскольку часы ПК могут идти назад. Queue ReceivedUtc —
время получения сообщения приложением, не исходного радио RX при offline backlog.
PC timestamp наблюдения также не равен точному времени radio IRQ ноды.

Нужен durable диагностический writer с bounded admission, SQL idempotency,
flush/retry/shutdown barrier и явным состоянием неполного захвата при перегрузке.
RX callback только копирует/ставит DTO в очередь. Повтор SQL не выполняет TX.
Retention raw трафика отдельно ограничивается; связанные хэши/метки времени
сохраняются до удаления истории сообщения. Clear-history удаляет message links
и ненужные связанные данные, не затрагивает независимую историю успешных маршрутов.

## Привязка наблюдений

Нельзя назначать последний raw последнему пузырьку: радио RX log, очередь
сообщений, ACK, повторы и разные контакты независимы. Нужны node/session scope,
точный crypto/payload match либо явный firmware correlation. Время — фильтр
кандидатов, но не доказательство. Копии одного хэша привязывать после доказанного
соответствия payload; учитывать возможность 64-битной коллизии.

RX ACK-пакеты — отдельный тип: их packet hash может отличаться у разных ACK
копий, а payload содержит ExpectedAck. Связь с исходным сообщением допустима
через существующий проверенный ACK matcher; ACK hash нельзя показывать как
hash переданного текста. Для обычного 0x82 исходный полный ACK packet недоступен.

## UI и этапы реализации

1. Typed RX log, radio parser/hash, immutable PC timestamp metadata и fixtures.
2. Миграция observations/receptions/links, durable ingress, details read API;
   согласовать со смысловым incoming dedup P7.
3. Канальные outgoing captures/расчёт и RX correlation. Для ЛС выбрать способ
   получения точной связи с Companion; не обещать полноту до решения этого пункта.
4. Общая карточка «Сведения о сообщении» через существующий modal host для обеих
   directions: loading/error/no hash, список всех попыток и всех hash events,
   время ПК с миллисекундами, источник, route/SNR/RSSI и копирование hash.
   Читать по захваченным NodeId/ConversationId/MessageId; смена чата и clear-history
   не переключают карточку на другое сообщение. Пагинация событий и refresh
   post-commit без изменения viewport/unread.
5. Эмуляция и ручной контроль сопоставления по внешнему радио-журналу.

Минимальные тесты: пользовательские raw vectors; transport codes/двух- и
трёхбайтовые path hashes; malformed raw; одинаковый hash в нескольких событиях;
разные hash попыток одного пузырька; перепутанный порядок RX/queue; offline backlog;
коллизии контактов/каналов; ACK hash отдельно; SQL retry без дубля/радиокоманды;
clear/shutdown; incoming сведения и темы modal.

## Проверка на предоставленном эксперименте

Одноразовый Python расчёт SHA-256(type + payload) без изменения исходников:
прямой пакет №1 и пакет №2 через репитер дали AF134BA9BFE83522; новое сообщение
№4 дало 052B140D081D5A06. Значения совпали с журналом пользователя. Это проверка
алгоритма выделения payload и hash, а не аппаратная приёмка нового функционала.


## Уточнение пользователя: сначала outgoing, RX позднее

05.10.2026: текущая задача — анализ сокращённого scope, без реализации.
Сохранение исходящих packet hashes и показ в сведениях предшествуют debug RX
и parser/correlation. Полный RX capture, incoming сведения и сетевые счётчики
отложены; их поддержка должна быть предусмотрена моделью данных.

Есть одно существенное противоречие в предположении «все исходящие можно
посчитать из уже известных полей»: для канала полный crypto input доступен через
session API; для ЛС отсутствует shared secret. Публичных ключей, timestamp,
attempt, текста и ExpectedAck недостаточно для воспроизведения ciphertext/MAC.
Поэтому минимальный вариант без изменения firmware/получения private key —
**вычисление хэшей отправленных канальных сообщений**, а ЛС пока показывают
«Хэш пакета недоступен» при сохранённых попытках/ACK. Нельзя подменять packet hash
самодельным digest текста или ACK-тегом. Поддержка всех исходящих ЛС потребует
отдельно выбранного источника crypto material либо расширения Companion.

Первый scope предлагается таким:

1. В MeshCoreSharp.dll — deterministic channel payload/hash calculator,
   проверяемый по firmware fixtures. При расчёте использовать точные timestamp,
   текущий channel secret, имя отправителя и TransmissionText; не заново
   оптимизировать OriginalText. Crypto не дублировать в UI/Core.
2. Миграция общего хранения packet observations/message links и read API.
   На этом шаге заполнять только calculated/command events исходящих каналов;
   nullable поля raw/path/SNR/RSSI оставлять пустыми. IncomingReceptions можно
   добавить с P7/debug RX, сохранив возможность связи в API/модели.
3. Capture и сохранение привязки к точному SendAttemptId перед Sending/TX,
   отдельно сохранение времени вызова отправки/принятия команды. SQL retry
   идемпотентен и не пересылает команду. Secret не сохраняется; fingerprint
   проверяется против captured binding до расчёта и отправки. Расчёт не должен
   быть обязательным условием отправки ЛС, для которых hash недоступен.
4. Outgoing «Сведения о сообщении»: все попытки/хэши с временем ПК, источник
   «Вычислен локально», состояние команды и копирование. Поддержка incoming
   будет следующим UI scope вместе с RX correlation.

Для повторить доставку канала один hash имеет несколько command events.
Отправить как новое — другой MessageId и обычно другой hash. Изменение имени
отправителя/ключа может изменить hash даже при прежнем timestamp/text: попытки
содержат фактический capture, прошлый hash не переписывается и не копируется
на новую передачу без проверки. Условия одинакового packet hash — одинаковый
wire payload, а не только одинаковые timestamp/text.

Для будущего RX нужна возможность принять Observation без MessageId, затем
добавить доказанную связь с исходящим attempt или входящим reception. Допускаются
observations до создания bubble/MSG_SENT и много observations на один hash.
Индексы NodeId+Hash(+SessionId/sequence) дают поиск кандидатов, но hash сам по себе
не назначается глобальным уникальным ключом. Source kind отделяет локальный
расчёт/вызов команды от реально услышанного пакета; только наблюдения RX
участвуют в метриках услышанного эфира. Все timestamps остаются неизменными,
истинными показаниями часов ПК; записи не переупорядочиваются по wire timestamp.

Сохранять raw/path descriptor/path bytes и сведения о hash width, а не только
готовый «число ретрансляторов». Позже вычислять число копий, distinct paths и
наблюдаемые префиксы последних ретрансляторов. Повтор одной копии не увеличивает
число distinct paths; 1/2/3-байтные префиксы маршрута могут сталкиваться и не
доказывают identity полного ключа. Счётчик — число наблюдённых путей/префиксов,
а не полная карта сети. Отсутствие эха не доказывает отсутствие ретрансляции:
у ноды/приложения могут быть пропуски RX, offline и неполный диагностический захват.

Критерии первого scope: vectors пользователя, exact UTF-8/prefix/padding,
повтор доставки с одинаковым hash и отдельными временами, новый timestamp,
изменение sender name, failure до TX, SQL replay без TX, offline details,
неизвестный hash у ЛС/старой истории, clear/backup совместимость.


## Решение пользователя — отложить

05.10.2026: хэши исходящих/входящих сообщений и debug RX отложены на неопределённое
время. Исследование сохранено для будущего возвращения к задаче; описанные таблицы
и API не реализованы. Текущий приоритет — P6 автоматических повторов ЛС.
