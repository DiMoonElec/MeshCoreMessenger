# План реализации MeshCoreMessenger

Дата: 25.09.2026. **Этап A выполнен: каркас, локальное хранилище, барьер
событий, один экземпляр на каталог данных и офлайн-загрузка истории готовы;
подключение приложения к ноде и пользовательский функционал не реализованы.**
Библиотечный фундамент уже реализован: USB/Serial, TCP, сообщения/ACK,
контакты, каналы, адверты, информация, статистика и барьер callbacks;
базовая проверка — 92 теста.
Архитектурные решения и инварианты: [MESSENGER_ARCHITECTURE.md](MESSENGER_ARCHITECTURE.md).
Этот план не заменяет [план библиотеки](../PLAN.md).

## A. Каркас, хранилище и граница событий

### A1 — каркас приложения (выполнено 25.09.2026)

- [x] Созданы `MeshCoreMessenger.Core`, `MeshCoreMessenger.Desktop` и два проекта
  xUnit v3; сохранена единственная Companion-библиотека `MeshCoreSharp.dll`.
- [x] Настроены ссылки `Desktop -> Core -> MeshCoreSharp` без доступа UI к internal runtime.
- [x] SDK зафиксирован на `10.0.301`; версии пакетов централизованы в
  `Directory.Packages.props`, для всех проектов созданы NuGet lock-файлы.
- [x] Добавлен базовый DI/logging bootstrap и минимальное Avalonia-окно с ViewModel.
- [x] Проверены restore в locked mode, Release build без предупреждений, два новых
  smoke-теста и прежние 82 регрессионных теста библиотеки.
- [x] Desktop реально запущен на macOS 26.6 arm64; старт DI/Avalonia подтверждён
  логом. Сетевые/Serial-клиенты не создавались, аппаратных передач не было.

Зафиксированные прямые пакеты: Avalonia `12.1.3`, CommunityToolkit.Mvvm `8.4.2`,
Microsoft.Data.Sqlite и Microsoft.Extensions `10.0.12`, xUnit v3 `4.0.1`.

Команды проверки A1:

```bash
dotnet restore MeshCoreSharp.sln --locked-mode
dotnet build MeshCoreSharp.sln -c Release --no-restore
dotnet test MeshCoreSharp.sln -c Release --no-build --no-restore
dotnet tests/MeshCoreSharp.Tests/bin/Release/net10.0/MeshCoreSharp.Tests.dll
dotnet run --project src/MeshCoreMessenger.Desktop/MeshCoreMessenger.Desktop.csproj -c Release --no-build --no-restore
```

### A2 — фундамент локального хранилища (выполнено 25.09.2026)

- [x] Добавлены `IAppPaths` и платформенные каталоги данных: Application Support
  на macOS, LocalApplicationData на Windows и XDG/fallback на Linux; БД называется
  `messenger.db`, резервные копии находятся в отдельном подкаталоге.
- [x] Реализованы один последовательный `DatabaseWorker` на отдельном потоке и
  отдельные короткоживущие read-only соединения без pooling.
- [x] Для SQLite включены `foreign_keys=ON`, WAL, `synchronous=FULL` и конечный
  `busy_timeout=5000`; добавлена транзакционная миграция версии 1.
- [x] Созданы таблицы и типизированные модели для settings, connection profiles,
  nodes, sessions, contacts, channels/bindings, conversations, messages,
  send attempts и drafts с внешними ключами, длинами ключей, уникальностью и индексами.
- [x] Реализованы stores настроек и профилей подключения, проверка целостности и
  версии при открытии. Более новая версия отклоняется до записи; ошибка миграции
  не удаляет и не пересоздаёт исходную БД.
- [x] Backup и restore используют SQLite backup API. Restore сначала проверяется
  и мигрируется на временной staging-БД, поэтому ошибочная миграция не заменяет
  рабочую БД; живая WAL-БД не копируется как одиночный файл.
- [x] На временных каталогах прошли 15 тестов хранилища и прежний Core smoke-тест;
  Release build прошёл без предупреждений, прежние 82 регрессионных теста
  библиотеки также прошли.
- [x] Подключение к Companion, reconnect, приём/отправка и аппаратные проверки не запускались.

Команды проверки A2:

```bash
dotnet build MeshCoreSharp.sln -c Release --no-restore --disable-build-servers
dotnet tests/MeshCoreMessenger.Core.Tests/bin/Release/net10.0/MeshCoreMessenger.Core.Tests.dll
dotnet tests/MeshCoreMessenger.Desktop.Tests/bin/Release/net10.0/MeshCoreMessenger.Desktop.Tests.dll
dotnet tests/MeshCoreSharp.Tests/bin/Release/net10.0/MeshCoreSharp.Tests.dll
```

### A3 — безопасный барьер событий библиотеки (выполнено 25.09.2026)

- [x] В публичный API `MeshCoreClient` добавлен `FlushEventsAsync(CancellationToken)`.
- [x] FIFO-маркер завершает вызов после возврата всех синхронных callbacks,
  поставленных до него; более поздние события конкретный вызов не задерживают.
- [x] Барьер не объявляет пустой очередь ноды, commit приложения или завершение
  самостоятельно запущенной обработчиком async-работы.
- [x] Отмена прекращает только ожидание вызывающего кода: принятый маркер остаётся
  в очереди, доставка событий и последующие барьеры продолжают работать.
- [x] После `DisconnectAsync` можно дождаться накопленных callbacks. После disposal
  новый барьер отклоняется с `ObjectDisposedException`; принятый до закрытия маркер
  продолжает дренироваться.
- [x] `DisconnectAsync`, `DisposeAsync`, RX, dispatcher и TCP/Serial не изменены;
  `DisposeAsync` по-прежнему не ждёт пользовательские callbacks неявно.
- [x] Добавлены 10 детерминированных regression tests; полный набор библиотеки —
  92/92, Release build — без предупреждений и ошибок.
- [x] Физическая нода и аппаратные передачи не использовались.

Команды проверки A3:

```bash
dotnet build MeshCoreSharp.sln -c Release --no-restore --disable-build-servers
dotnet tests/MeshCoreSharp.Tests/bin/Release/net10.0/MeshCoreSharp.Tests.dll
```

### A4.1 — один экземпляр на каталог данных (выполнено 25.09.2026)

- [x] До запуска Avalonia приложение открывает `.meshcoremessenger.lock` внутри
  фактического `IAppPaths.DataDirectory` с `FileShare.None` и удерживает handle
  в течение всего процесса.
- [x] Разные каталоги имеют независимые блокировки. Постоянный lock-файл сам по
  себе не блокирует запуск; штатное или аварийное закрытие handle освобождает OS-lock.
- [x] Второй процесс для того же каталога завершается до обычного startup с кодом
  `2` и диагностикой; недоступный каталог получает отдельную ошибку и код `3`.
- [x] Добавлены 5 тестов механизма; вместе с bootstrap smoke-тестом Desktop — 6/6.
- [x] На macOS вручную проверены два одновременных процесса: второй отклонён, после
  принудительного завершения первого новый процесс успешно открыл приложение.
- [x] SQLite, MeshCore-сессия, reconnect и аппаратные интерфейсы не запускались.

Команды проверки A4.1:

```bash
dotnet build MeshCoreSharp.sln -c Release --no-restore --disable-build-servers
dotnet tests/MeshCoreMessenger.Desktop.Tests/bin/Release/net10.0/MeshCoreMessenger.Desktop.Tests.dll
dotnet tests/MeshCoreMessenger.Core.Tests/bin/Release/net10.0/MeshCoreMessenger.Core.Tests.dll
dotnet tests/MeshCoreSharp.Tests/bin/Release/net10.0/MeshCoreSharp.Tests.dll
```

### A4.2 — офлайн-загрузка истории (выполнено 25.09.2026)

- [x] Добавлен ограниченный read-only `ILocalHistoryReader`: список диалогов и
  страницы сообщений читаются через отдельные короткоживущие SQLite-соединения.
- [x] Диалоги сортируются по последнему сообщению; страницы сообщений имеют
  курсор `LocalSequence`, возвращаются хронологически и не смешивают ноды/диалоги.
- [x] Startup соблюдает порядок: каталог данных -> instance lock -> открытие и
  миграция SQLite -> DI и начальная история -> Avalonia. Второй экземпляр не
  открывает БД; ошибка открытия получает диагностику и код завершения `4`.
- [x] Минимальный офлайн-shell показывает список диалогов, последние 100 сообщений
  выбранного диалога, пустое состояние, ошибку чтения и статус «Не подключено».
- [x] При выходе отменяются и завершаются текущие чтения, затем закрывается writer/БД
  и только после этого освобождается блокировка каталога.
- [x] Core-тесты — 23/23, Desktop-тесты — 10/10, регрессии библиотеки — 92/92;
  полный Release build прошёл без предупреждений.
- [x] Desktop запущен на macOS arm64 с локальной БД без Companion-сессии;
  TCP/Serial, физическая нода и аппаратные передачи не использовались.

Готово, когда БД/настройки переживают рестарт, миграции и backup проходят на
временных каталогах; неподдерживаемая/повреждённая БД не уничтожается;
барьер проверен с задержанным обработчиком и отключением RX; регрессии библиотеки
проходят; сохранённая история доступна до подключения. Новых радиопередач для
этапа не требуется. **Этап A завершён 25.09.2026.**

## B. Подключение и запись входящих

- [x] Профили TCP/Serial, выбор порта, сохранение настроек, автоподключение.
- [x] ConnectionSupervisor: state machine, отмена, backoff с jitter,
  ручная остановка, смена профиля и сон/пробуждение.
- [x] Отдельные SessionId/NodeId, проверка ключа ноды после Start, защита от
  поздних событий прежней сессии.
- [x] AutoReceiveMessages=false, загрузка справочников до начального drain,
  объединение push-сигналов и безопасная реакция на тайм-аут.
- [x] MessageIngestor и writer: commit до UI, обработка неразрешённых отправителей,
  неопределённых канальных слотов и ошибок диска.
- [ ] Штатное закрытие: stop RX -> event barrier -> commit -> dispose/закрытие БД.
- [x] Открытие приложения offline показывает историю и состояние подключения.

### Разбиение этапа B

Исходный checklist выше сохраняется как критерий всего этапа. Реализовывать B следует
последовательно подэтапами B1–B7: каждый из них имеет отдельную проверяемую границу,
а завершение одного подэтапа не означает выполнения исходного пункта целиком, если
его части отнесены к следующим подэтапам.

Анализ текущего кода: публичного API `MeshCoreSharp` для B достаточно — есть
`AutoReceiveMessages=false`, события пакетов/сообщений/ошибок, `GetContactsAsync`,
`GetChannelsAsync`, `DrainMessagesAsync`, `DisconnectAsync` и `FlushEventsAsync`.
Нового протокольного API не планируется. В Messenger пока отсутствуют session/
directory/ingest/supervisor services и соответствующие write repositories. Начальная
схема БД содержит нужные сущности, но перед сохранением всех полей входящих требует
одной дополняющей миграции, описанной в B4.

| Исходный пункт | Подэтапы |
| --- | --- |
| Профили, порты, настройки, автоподключение | B1, B6, B7 |
| ConnectionSupervisor, retry, смена профиля, сон | B6 |
| SessionId/NodeId и защита поколений | B2, B6 |
| Ручной drain после справочников | B3, B5 |
| MessageIngestor и commit до UI | B4, B5 |
| Штатное закрытие | B2 (примитивы), B7 (полная последовательность) |
| Offline-история и состояние | A4.2, B7 |

#### B1 — профили подключения и платформенные адаптеры (выполнено 25.09.2026)

**Цель:** пользователь может создать, изменить и выбрать корректный TCP/Serial
профиль; приложение способно построить транспорт по профилю, но ещё не запускает
фоновое подключение.

- Расширить `IConnectionProfileStore` чтением списка профилей. Последний выбранный
  `ProfileId` хранить через `ISettingsStore`; отсутствующий профиль не заменять
  первым найденным без явного решения пользователя.
- Добавить application service для создания/валидации профилей. Сохранять TCP host
  и явный port либо Serial port, baud rate, DTR/RTS, open delay, command/ACK timeout,
  `AutoConnect`, `Reconnect` и ожидаемый полный ключ ноды. Транспортные поля разных
  типов не смешивать.
- Добавить `ISerialPortCatalog` в Core и платформенную реализацию в Desktop.
  Результат сортировать и удалять дубликаты; сохранённый, но временно отсутствующий
  порт оставлять видимым. Перечисление портов не должно открывать их или менять линии.
- Добавить фабрику транспорта/клиента с тестовым seam. Маппинг должен точно переносить
  Serial `DtrEnable`, `RtsEnable`, baud rate и open delay; линии задаются один раз при
  открытии транспортом. Для приложения заранее фиксируется
  `MeshCoreClientOptions.AutoReceiveMessages=false`.
- Добавить минимальный экран/панель профиля: список, TCP/Serial поля, обновление
  списка портов, сохранить и выбрать. Подключение и retry в этом подэтапе не запускать;
  флаги автоподключения лишь сохраняются для B6.

Обязательные тесты B1: создание/изменение/список профилей, восстановление выбранного
профиля, валидация взаимоисключающих полей и границ портов/тайм-аутов, точный маппинг TCP и
Serial options, отсутствующий сохранённый Serial-порт, отмена перечисления портов.
Использовать временную БД и fake catalog/factory; физический порт не открывать.

Реализовано: менеджер профилей хранит выбранный `ProfileId` отдельно от списка и
не выбирает другой профиль при потере ссылки; фабрика точно отображает TCP/Serial
настройки в transport/client с `AutoReceiveMessages=false`; системный каталог портов
только перечисляет имена, а сохранённый отсутствующий порт остаётся доступен в
редакторе. В Desktop добавлена минимальная панель списка и редактирования профилей.
Подключение, retry и применение `AutoConnect`/`Reconnect` оставлены для B6.

Проверено: Release build всей solution; 32 Core tests, 18 Desktop tests и 92
регрессионных теста `MeshCoreSharp`. Фабрика протестирована с несуществующим Serial
port: построение клиента не открывает порт. Физическая нода и радиопередача не
использовались.

#### B2 — одна Companion-сессия и идентичность локальной ноды (реализовано 25.09.2026)

**Цель:** выполнить одну отменяемую попытку `Connect -> Start -> Identify` без retry,
привязать её к устойчивой идентичности ноды и корректно закрыть.

- Добавить хранилища `Nodes` и `Sessions`: поиск/создание ноды только по полному
  32-byte `SelfInfo.PublicKey`, создание `SessionId` до подключения, привязка `NodeId`
  после идентификации и сохранение `EndedUtc`/причины завершения. Адрес, имя профиля
  и имя ноды не являются идентичностью.
- Реализовать `CompanionSession` и тестируемую обёртку/фабрику над публичным
  `MeshCoreClient`. Одна попытка владеет ровно одним новым transport/client,
  неизменяемыми `SessionId` и generation. Подписки ставятся до `ConnectAsync` и
  `StartAsync`; события быстро копируются в DTO приложения и помечаются SessionId.
- Всегда создавать клиент с `AutoReceiveMessages=false`. На B2 не читать контакты,
  каналы и очередь сообщений, не отправлять команды изменения/передачи.
- Если `ExpectedNodePublicKey` пуст, после успешной первой идентификации сохранить
  полный ключ в профиль. Если ключ отличается, перейти в `NeedsAttention`, не менять
  ожидаемый ключ, не продолжать синхронизацию и предоставить явное решение
  «использовать эту ноду как новую привязку» либо остановить попытку.
- Закрывающий примитив сессии уже должен соблюдать `DisconnectAsync ->
  FlushEventsAsync -> отписка -> DisposeAsync`; application writer и закрытие БД
  будут добавлены в B7.

Обязательные тесты B2: subscribe-before-connect/start; SessionId существует до
первого callback; один полный ключ через разные профили даёт один NodeId; другой
ключ по тому же адресу не смешивает историю; mismatch не обновляет профиль и не
запускает drain; отмена/ошибка помечает сессию завершённой; поздний lifecycle callback
сохраняет исходный SessionId. Все тесты — fake client, без сети и Serial.

Реализовано: `CompanionSessionFactory` создаёт запись сессии до клиента и callbacks,
а `CompanionSession` владеет одной попыткой и неизменяемыми `SessionId`/generation.
Все публичные события клиента копируются в DTO очереди с идентичностью попытки;
буферы сообщений, адвертов и raw-пакетов не разделяют изменяемую память библиотеки.
Ноды ищутся только по полному 32-byte ключу. Первая идентификация привязывает профиль,
а несовпадение переводит сессию в `NeedsAttention` до явного
`UseConnectedNodeAsync`. Закрытие соблюдает `DisconnectAsync -> FlushEventsAsync ->
отписка -> DisposeAsync` и завершает запись сессии даже при ошибке disconnect.

Проверено: Release build без предупреждений; 41 Core test, 18 Desktop tests и 92
regression tests `MeshCoreSharp`. Аппаратный прогон через
`/dev/cu.usbserial-0001` успешно выполнил `Connect -> APP_START -> Disconnect`,
создал и завершил запись сессии, определил NodeId по полному ключу и привязал ранее
пустой профиль. Drain и RF-команды не выполнялись; порт после теста освобождён.
Подробности — в [отчёте](testing/messenger-b2-serial-2026-09-25.md).

#### B3 — снимки контактов, каналов и версии привязок слотов (реализовано 25.09.2026)

**Цель:** после идентификации получить полный справочник, необходимый для безопасного
разрешения входящих сообщений, и только затем разрешить начальный drain.

- Добавить репозитории и `DirectoryService` для транзакционного сохранения полного
  снимка контактов и каналов текущего NodeId. Неполный/ошибочный `GetContactsAsync`
  или `GetChannelsAsync` не должен помечать прежний справочник отсутствующим.
- Контакт хранить по `(NodeId, full public key)` со всеми шестью байтами префикса,
  типом, flags, путём и advert-метаданными. В разрешении префикса участвуют все
  актуальные типы контактов, а не только Chat; исторический `PresentOnNode=false`
  не делает контакт текущим адресатом.
- Для каждого непустого канала вычислять SHA-256 fingerprint полного 16-byte secret;
  сам secret в БД не сохранять и не логировать. Hashtag распознавать только сравнением
  с `ChannelSecrets.DeriveHashtag(name)`; остальные импортированные каналы без
  проверенного источника оставлять `Unknown`, а не угадывать по имени.
- Сопоставить новый снимок с активными `ChannelBindings`. Неизменённая пара
  slot/fingerprint продолжает текущую generation. Новый/изменённый slot формирует
  план перехода. При изменении во время offline старую binding не закрывать до
  окончания начального drain: backlog такого slot должен идти в «Канал не определён».
  После успешного drain и commit атомарно закрыть старую и активировать новую binding.
- До успешного полного снимка и подготовки плана перехода состояние не становится
  `Synchronizing`/`Online`, передача остаётся недоступной.

Обязательные тесты B3: полный снимок и повторный идентичный снимок; rename с тем же
secret сохраняет ChannelId; новый secret того же slot создаёт новую generation;
одинаковые имена с разными secret не объединяются; одинаковый secret в двух slots
даёт один ChannelId и две привязки; ошибка середины чтения сохраняет прежний снимок;
secret отсутствует в БД и логах; offline-смена slot создаёт pending transition.

Реализовано: `DirectoryService` сначала полностью получает contacts и channels, затем
передаёт в единую SQLite-транзакцию только нормализованные записи. Контакты снабжены
полными ключами и 6-byte prefix; отсутствующие в полном снимке записи сохраняются как
`PresentOnNode=false`. Для каналов сохраняется лишь SHA-256 fingerprint 16-byte
секрета. Hashtag получает `PublicOrHashtag` только при точном совпадении с
`ChannelSecrets.DeriveHashtag(name)`, остальные импортированные каналы остаются
`Unknown`. Изменённый или удалённый активный slot формирует in-memory pending
transition; B5 применит его транзакционно после initial drain.

Проверено: Release build без предупреждений; 48 Core tests, 18 Desktop tests и 92
regression tests `MeshCoreSharp`. Аппаратная проверка получила 117 контактов и 4
активные channel bindings через локальные запросы; drain, изменение конфигурации и
RF-команды не выполнялись. Подробности — в
[отчёте](testing/messenger-b3-serial-2026-09-25.md).

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

#### B6 — ConnectionSupervisor, автоподключение и reconnect (выполнено 28.09.2026)

**Цель:** сделать единственного владельца активной попытки и всех переходов
`Offline/Connecting/Identifying/Synchronizing/Online/RetryWaiting/Disconnecting/
NeedsAttention`.

- Supervisor имеет один сериализованный control loop и один generation token.
  `StartAutoConnect`, `ConnectNow`, `Disconnect`, `SwitchProfile`, shutdown и wake
  посылают команды этому loop; они не создают параллельные reconnect-задачи.
- При старте после показа offline-истории читать выбранный профиль и запускать одну
  фоновую попытку только при `AutoConnect=true`. Ручное отключение отменяет попытку
  и retry до явного Connect либо следующего запуска. Смена профиля дожидается полного
  закрытия старой сессии до открытия новой.
- Реализовать задержки 1, 2, 4, 8, 15, 30 секунд, затем 30 секунд с jitter ±20%; после
  60 секунд устойчивого Online сбрасывать счётчик. Время, delay и источник jitter
  внедряются интерфейсами для детерминированных тестов.
- К retry относятся временные TCP/Serial ошибки, unplug и занятый/отсутствующий порт.
  Невалидный профиль, неожиданный ключ ноды, несовместимая прошивка и ошибка БД
  переходят в `NeedsAttention` без цикла. Причина и время следующей попытки входят
  в immutable state snapshot для UI.
- State callbacks старой generation не меняют новую state machine. Уже принятые
  сообщения старой сессии при этом не отбрасываются: B4 завершает их по сохранённым
  SessionId/NodeId.
- Добавить `IPlatformPowerEvents`: при suspend остановить/пометить сессию, при wake
  сериализованно проверить или пересоздать соединение; открытый socket/port сам по
  себе не считать доказательством связи. Реализации macOS/Windows держать в Desktop,
  Linux — через тот же интерфейс без отдельной модели приложения.

Обязательные тесты B6: точная последовательность состояний; только одна активная
попытка; deterministic backoff/jitter/reset; ConnectNow прерывает delay без второго
loop; ручной stop; смена профиля; transient/permanent classification; late callback
старой generation; suspend/wake; shutdown во время Connecting, Synchronizing и
RetryWaiting. Тесты используют fake session/time/power events.

##### B6.1 — базовый supervisor и reconnect (выполнено 28.09.2026)

Реализован один сериализованный control loop с immutable snapshot состояний
`Offline/Connecting/Identifying/Synchronizing/Online/RetryWaiting/Disconnecting/
NeedsAttention`. `StartAutoConnect`, `ConnectNow`, ручное отключение и shutdown не
создают параллельных циклов. Каждая новая generation получает новый
`CompanionSession` и `ReceiveCoordinator`; следующая попытка начинается только после
полного завершения предыдущей.

Attempt lifecycle передаётся supervisor отдельным сигналом и не читает
`CompanionSession.Events`: единственным consumer этой очереди остаётся
`ReceiveCoordinator`. Закрытие разделено на quiesce новых drain, остановку сессии с
library event barrier, дочитывание application events и ingest barrier. Событие,
пришедшее во время финального session barrier, покрыто отдельным regression test и
коммитится до завершения attempt.

Добавлены transient/permanent classification, учёт `Reconnect=false`, задержки
1/2/4/8/15/30 секунд с injectable jitter ±20%, сброс backoff после 60 секунд Online
и generation guard для поздних progress/lifecycle callbacks. Timeout
`SYNC_NEXT_MESSAGE` закрывает старую session и создаёт новую; mismatch ключа,
ошибки БД/ingest и несовместимые/невалидные настройки переходят в `NeedsAttention`
без reconnect loop. Supervisor не имеет API отправки, advert или mutation.

Проверено: полный Release build без предупреждений; Core tests — 80/80, Desktop —
18/18, MeshCoreSharp regression suite — 92/92. Тесты используют fake attempts,
управляемое время/delay/jitter и временную SQLite. Физическая нода не использовалась.

В B6.1 намеренно не входили смена профиля, suspend/wake, platform power adapters,
Desktop startup/autoconnect и UI состояния. Первые три части выполнены в B6.2;
Desktop-интеграция и исходный checklist всего этапа B остаются для B7.

##### B6.2 — смена профиля и suspend/wake (выполнено 28.09.2026)

Добавлен `SwitchProfile`: существующий профиль выбирается отдельной операцией без
перезаписи его полей, retry отменяется, а новая attempt/generation создаётся только
после полного `StopAsync`/dispose предыдущей. Смена во время suspend только сохраняет
выбор; соединение открывается после wake. Поздние callbacks прежнего профиля
отсекаются тем же generation guard, параллельных attempts нет.

Добавлен `IPlatformPowerEvents`. Его callbacks только ставят suspend/wake в общий
control loop. Suspend отменяет startup/retry или полностью закрывает Online attempt;
wake всегда создаёт новую session и не доверяет прежнему socket/port. Manual
disconnect остаётся Offline, а `NeedsAttention` сохраняется без автоматического
reconnect. Повторные power-события идемпотентны; shutdown отписывает supervisor.

В Desktop реализованы adapters Windows через message-only window и
`WM_POWERBROADCAST`, macOS через `IORegisterForSystemPower`/CFRunLoop с обязательным
acknowledgement sleep, а для остальных платформ — no-op того же интерфейса. Новых
NuGet-зависимостей нет. macOS adapter прошёл локальный start/stop smoke-test;
Windows message mapping проверен детерминированно, но на Windows не запускался.

Проверено: полный Release build без предупреждений; Core tests — 94/94, Desktop —
21/21, MeshCoreSharp regression suite — 92/92. Fake-тесты покрывают switch из Online
и RetryWaiting, switch во время suspend, suspend/wake из Connecting, Synchronizing,
Online и RetryWaiting, duplicate wake, manual disconnect, `NeedsAttention`, late
callback и отсутствие перекрывающихся attempts. Физическая нода не использовалась.

Регистрация supervisor/power events в Desktop DI, startup/autoconnect и UI состояния
намеренно не добавлены: это граница B7. SQLite schema и MeshCoreSharp/protocol не
изменялись.

#### B7 — интеграция Desktop, штатное закрытие и приёмка Stage B

**Цель:** приложение автоматически подключается после показа локальной истории,
записывает входящие до обновления UI и безопасно завершает всю цепочку владения.

- Зарегистрировать сервисы B1–B6 в DI. MainWindow показывает выбранный профиль,
  immutable состояние supervisor, причину/время retry и остаётся пригодным для
  чтения offline. Полный Telegram-подобный UI, непрочитанное и отправка остаются C/D.
- Startup не ждёт сеть: instance lock и БД открываются как в A, окно получает историю,
  затем supervisor запускается фоном. Обычный запуск не отправляет сообщения,
  advert, mutation, установку времени или иные радиокоманды.
- Штатный выход/смена профиля: запретить новые сценарии и retry; остановить
  ReceiveCoordinator; `DisconnectAsync`; дождаться observers; `FlushEventsAsync`
  при ещё активных подписках; `MessageIngestor.FlushAsync`/commit; записать конец
  Session; отписаться и Dispose client/transport; остановить application writer;
  закрыть SQLite; освободить instance lock.
- Если финальный commit не удался, отменить обычное закрытие окна, сохранить writer
  и pending DTO живыми, показать ошибку и дать повторить. Отдельно документировать,
  что принудительное завершение ОС может потерять уже удалённый из ноды, но ещё не
  закоммиченный остаток.
- Добавить end-to-end harness с fake Companion и временной SQLite: события во время
  startup, directory load, reconnect и shutdown должны оказаться в правильной истории;
  два цикла supervisor и автоматическая передача недопустимы.
- После всех fake-тестов провести аппаратную приёмку без передачи в эфир: Serial с
  сохранёнными DTR/RTS без reset по uptime, чтение накопленной очереди, unplug/replug,
  повторный запуск, по возможности TCP к той же ноде. Зафиксировать ОС, firmware,
  профиль и результат в `docs/testing/`. Никаких тестовых сообщений или advert для B.

Обязательные тесты B7: offline startup не блокируется сетью; commit-before-UI;
сообщение на границах startup/shutdown; успешный и неуспешный flush; повтор закрытия;
освобождение DB/instance lock; история доступна после рестарта; полный Release build,
все Core/Desktop/MeshCoreSharp regression tests. Stage B завершать только после
документированной аппаратной проверки либо явно оставить её непроверенным блокером.

##### B7.1 — composition root и неблокирующий startup (выполнено 28.09.2026)

- Зарегистрировать attempt factory, supervisor, retry policy и platform power events;
  `ReceiveCoordinator` не регистрировать singleton, поскольку он принадлежит одной
  attempt и создаётся её factory.
- Добавить единственного Desktop lifecycle owner с идемпотентными startup/shutdown.
  Локальную историю загрузить до сетевой политики, а `StartAutoConnect` вызвать после
  показа окна. Ожидание сети/Serial не должно блокировать UI startup.
- При обычном завершении сначала остановить UI-сценарии, затем supervisor, после него
  асинхронно закрыть DI services, SQLite и instance lock. Отменяемое закрытие при
  ошибке commit будет добавлено в B7.3.

Тесты B7.1: полный DI graph; один lifecycle/supervisor; ровно один startup/shutdown;
новый coordinator на attempt; отсутствие transport attempt при выключенном
`AutoConnect`; заблокированный connect не задерживает возврат startup-команды.

Реализовано: Desktop DI регистрирует production attempt factory, failure classifier,
delay/jitter, platform power events и единственный supervisor. Лишняя singleton-
регистрация `ReceiveCoordinator` удалена: coordinator по-прежнему создаётся factory
отдельно для каждой attempt. `DesktopConnectionLifecycle` объединяет повторные
startup/shutdown вызовы и при гонке дожидается принятого startup перед shutdown.

`Program` сначала открывает lock/SQLite и загружает локальную историю. Lifecycle
создаётся только после этого, а `StartAutoConnect` вызывается обработчиком первого
`Window.Opened`; создание transport/client остаётся фоновой работой supervisor и не
задерживает UI. При обычном выходе останавливаются ViewModels, затем supervisor,
после чего DI services закрываются через `DisposeAsync`, далее SQLite и instance lock.
Отмена уже начавшегося закрытия при persistence failure намеренно остаётся B7.3.

Проверено: полный Release build без предупреждений; Core tests — 94/94, Desktop —
26/26, MeshCoreSharp regression suite — 92/92. Новые fake-тесты подтверждают один
startup/shutdown, их порядок, возврат startup при заблокированном создании connection
attempt и отсутствие attempt при `AutoConnect=false`; bootstrap test проверяет полный
DI graph и отсутствие singleton coordinator. Физическая нода не использовалась.

##### B7.2 — состояние подключения и commit-driven UI (выполнено 28.09.2026)

- Проецировать immutable supervisor snapshot, выбранный профиль, причину и время
  retry в MainWindow; добавить минимальные Connect/Disconnect действия и применение
  `SwitchProfile` после явной смены профиля.
- `MessageCommitted` обрабатывать только через UI dispatcher и перечитывать SQLite
  после commit; объединять частые обновления и отписываться при остановке ViewModel.

Тесты B7.2: отображение состояний; UI-thread dispatch; profile switch; отсутствие
обновления до commit; обновление истории после commit; поздние callbacks после stop.

Реализовано: `MainWindowViewModel` проецирует immutable snapshot supervisor во все
восемь пользовательских состояний, показывает причину и время следующей попытки,
а также предоставляет минимальные команды Connect/Disconnect. После явного
`SaveAndSelect` профиль передаётся supervisor через `SwitchProfileAsync`; выбор в
редакторе сам по себе подключения не меняет.

Desktop подписывается только на post-commit событие `MessageIngestor` через узкую
notification-границу. Фоновый coalescing worker после сигнала перечитывает историю
через `ILocalHistoryReader` и применяет коллекции через UI dispatcher. При остановке
обе подписки снимаются до отмены worker; уже поставленные и поздние callbacks не
могут изменить остановленную ViewModel. Прямого чтения `CompanionSession.Events` и
автоматических protocol mutations эта проекция не добавляет.

Проверено: полный Release build без предупреждений; Core tests — 94/94, Desktop —
40/40, MeshCoreSharp regression suite — 92/92. Fake-тесты покрывают точное
отображение всех состояний, обязательный UI dispatch, Connect/Disconnect, profile
switch, отсутствие reload для не вставленного дубликата, перечитывание истории после
commit, объединение частых commit-сигналов и игнорирование callbacks после stop.
Физическая нода не использовалась.

##### B7.3 — отменяемое и восстанавливаемое закрытие

- Перестать подавлять persistence failure в shutdown contract. Закрытие должно
  сообщать результат и сохранять возможность повторить durable session-end/ingest
  flush до уничтожения writer.
- Перехватить Avalonia shutdown/closing: обычное закрытие отменяется на время
  quiesce/barriers/commit. При ошибке окно, pending DTO, SQLite и instance lock
  остаются живыми; после явного retry закрытие повторяется. Параллельные запросы
  закрытия объединяются в одну операцию. OS shutdown остаётся документированным
  best-effort сценарием.

Тесты B7.3: сообщение на границе shutdown; успешный и неуспешный flush; retry без
потери pending DTO; повтор закрытия; session end; точный порядок disposal; освобождение
БД и instance lock только после успеха; история доступна после рестарта.

##### B7.4 — end-to-end и аппаратная приёмка

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

## C. Удобный интерфейс чтения

- [ ] Двухпанельное окно; «Личные», «Каналы», «Устройства»; отдельная группа
  неизвестных личных отправителей, read-only сведения служебных контактов.
- [ ] Фильтры публичных/hashtag/с секретом/неизвестных каналов без угадывания по имени.
- [ ] Виртуализированная история и пагинация, непрочитанное, время/превью,
  копирование, поиск, переход к последнему/первому непрочитанному.
- [ ] Сохраняемые черновики, тема, положение окна, клавиатура/Cmd/Ctrl, DPI/IME.
- [ ] Постоянный статус ноды/соединения и понятные ошибки без модального спама.

Готово, когда можно читать накопленную историю offline и новые сообщения online;
100 000 тестовых сообщений не загружаются сразу в UI; Chat не смешивается с
Repeater/Room/Sensor; непрочитанное и черновики восстанавливаются после рестарта.

## D. Отправка и управление кругом общения

- [ ] Общий helper UTF-8 лимита/валидации в библиотеке; UI использует его.
- [ ] Подготовка исходящего в БД, одна передача, статусы MSG_SENT/ACK/timeout/error.
- [ ] Восстановление Prepared/Unknown после аварии без автоматической отправки;
  явный повтор с сохранением отдельной попытки.
- [ ] Добавление Chat из NEW_ADVERT, обновление/удаление контакта с readback;
  проверка полной идентичности и коллизии шестибайтового префикса.
- [ ] Добавление hashtag или канала с явным 16-byte secret, readback, очистка,
  versioned slot bindings и сохранение истории удалённого канала.
- [ ] Отдельное удаление локальной истории; подтверждение операций удаления.
- [ ] Ручной адверт; открытие окна/подключение не отправляет его самостоятельно.

Готово, когда на двух нодах работает двусторонний канал и личный чат, доставка
отображается честно, неуспешная передача не повторяется скрыто, удаление контакта
не удаляет историю, смена секрета слота не смешивает чаты. Тестовый эфир — только
согласованные адресаты и помеченный текст разработки.

## E. Приёмка первой пригодной к использованию версии

- [ ] Пройти матрицу ниже на macOS и Windows; записать firmware, ОС, архитектуру,
  транспорт, результаты и известные ограничения в `docs/testing/`.
- [ ] Собрать self-contained пакеты, запустить на машинах без .NET SDK,
  проверить native SQLite/Serial и сохранение истории при обновлении пакета.
- [ ] Проверить backup/restore, отсутствие ключей/текстов в обычных логах,
  редактирование профиля без побочных радиоизменений.
- [ ] Добавить короткую инструкцию пользователя: первый запуск, профили,
  статусы доставки, резервная копия, расположение данных, известные ограничения.

**Первый релиз для повседневного использования = A–E.** Красивый UI с одной
успешной отправкой не заменяет приёмку сохранности и восстановления соединения.

## F. После первой версии

- [ ] Журнал Companion-пакетов с известными полями и безопасным hex, кольцевой
  буфер, фильтры, счётчик пропусков. Можно делать после D, он не блокирует E.
- [ ] Системные уведомления/звук и mute, архив чатов, USB identification.
- [ ] Разбор LOG_DATA/RAW_DATA после проверки прошивки; отдельно оценить
  доступность радиопакетов и расшифровки. Не обещать результат заранее.
- [ ] По потребности: FTS-поиск, экспорт выбранной переписки, расширенное
  ограничение очередей библиотеки и durable receive sink.

BLE, room-server login, удалённая телеметрия/CLI, вложения, геокарта,
реакции/редактирование сообщений, облачная синхронизация, автообновление и
одновременные подключения нескольких нод не входят в этот план первой версии.

## Матрица обязательных проверок

| Сценарий | Проверяемое поведение |
| --- | --- |
| Запуск без сети/ноды | История доступна, одна фоновая попытка, понятная причина/задержка |
| Отмена retry, смена профиля | Старый транспорт освобождён; поздний callback не меняет новую сессию |
| Та же нода через TCP/Serial | Та же история по полному SelfInfo.PublicKey |
| Другая нода по тому же адресу | Нет смешивания истории, требуется новая привязка |
| Сообщение при старте/закрытии | Подписки заранее; барьер и commit сохраняют уже доставленные callbacks |
| Быстрый ACK / разрыв до MSG_SENT | Delivered не затирается Accepted; неопределённый исход сохраняется Unknown |
| Разрыв после MSG_SENT / рестарт | Никакой автоматической передачи, новая попытка только вручную |
| Пропущенный ACK | Unconfirmed, приём других сообщений продолжается |
| Канальный OK | Показано принятие нодой, не подтверждение всеми участниками |
| Disk full / read-only DB | Передачи/новые drain остановлены, явная ошибка, история не удаляется |
| Повтор события writer-ом / одинаковые эфирные тексты | EventId идемпотентен, два независимых сообщения не склеены |
| Нет контакта / коллизия префикса | Сообщение сохранено, ложный адресат не выбран, отправка заблокирована |
| Rename / remove / re-add контакт | История остаётся привязана к полному ключу |
| Смена секрета / перестановка слотов | Чаты не склеены, версия привязки сохранена |
| Смена слота сторонним приложением offline | Начальная неоднозначная очередь помечена, не приписана новому каналу |
| Ошибка mutation/readback | UI не утверждает успех; повторное чтение перед новым действием |
| Отмена закрытия из-за ошибки сохранения | Writer жив, можно повторить запись, pending данные не потеряны молча |
| Повреждённая/более новая БД | Понятный отказ без удаления и автоматического downgrade |
| 100 000 сообщений | Пагинация, ограниченные коллекции, отзывчивый UI |
| USB unplug/replug, TCP, сон | Reconnect без циклов сброса, история/статусы сохранены |
| Windows/macOS упаковка | Запуск без SDK, Serial native libs, Unicode, DPI, пути данных |

Тестировать Core с временной SQLite и fake Companion; задержки/retry подменять
управляемым временем. Полезные UI-тесты — выбор вкладок, непрочитанное,
недоступность отправки offline, навигация и отображение статусов. Не привязывать
unit-тесты к реальным радиопередачам. Аппаратная приёмка проводится отдельно.

## Стартовое задание для реализации

> Прочитай AGENTS.md, docs/MESSENGER_ARCHITECTURE.md, docs/MESSENGER_PLAN.md,
> docs/ARCHITECTURE.md и docs/COMPANION_PROTOCOL.md. Каркас A1 уже реализован:
> не пересоздавай проекты, версии, lock-файлы и bootstrap. Выполни только следующий
> подэтап, явно указанный пользователем, сохраняя единственную Companion-библиотеку.
> Сначала проверь текущий код и статус репозитория; новые имена из документа —
> предложения. Обнови checklist с фактическими результатами. Аппаратные передачи
> этим планом не разрешаются автоматически.
