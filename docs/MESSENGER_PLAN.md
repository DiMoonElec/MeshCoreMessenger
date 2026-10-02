# План реализации MeshCoreMessenger

Актуализация: 02.10.2026. **Этапы A и B выполнены, включая B7.5: локальное
хранилище, offline startup, приём, reconnect и восстанавливаемое закрытие готовы.
Профиль описывает транспорт; identity определяется по полному ключу в каждой
session. Stage C начат: C1–C9 завершены автоматически; ручная UI-проверка C7–C9
ожидается. Перед C10 согласована компонентная переработка UI; реализован первый
визуальный шаг UI1 (NavigationShellView), ручная проверка ожидается. C10 не начат.**
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
- [x] Отдельные SessionId/NodeId, определение ноды по полному ключу после Start, защита от
  поздних событий прежней сессии.
- [x] AutoReceiveMessages=false, загрузка справочников до начального drain,
  объединение push-сигналов и безопасная реакция на тайм-аут.
- [x] MessageIngestor и writer: commit до UI, обработка неразрешённых отправителей,
  неопределённых канальных слотов и ошибок диска.
- [x] Штатное закрытие: stop RX -> event barrier -> commit -> dispose/закрытие БД.
- [x] Открытие приложения offline показывает историю и состояние подключения.

### Разбиение этапа B

Исходный checklist выше сохраняется как критерий всего этапа. Реализовывать B следует
последовательно подэтапами B1–B7 и завершающей корректировкой B7.5: каждый из них имеет отдельную проверяемую границу,
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

Описания B1, B2 и B6 ниже сохраняют решения, принятые и проверенные на момент их
выполнения. Последующий аудит обнаружил лишнюю связь connection profile с identity
ноды; B7.5 явно отменяет только эту часть, не переписывая историю этапов задним числом.

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

Принятая здесь привязка profile -> expected node key позднее отменена в B7.5.
Устойчивое определение `NodeId` по полному ключу и разделение истории сохранены.

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

Классификация неожиданного ключа как permanent failure позднее отменена в B7.5:
другой ключ через тот же endpoint теперь является нормальным результатом Identify.
Остальные правила `NeedsAttention`, reconnect и generation guard не изменены.

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

##### B7.3 — отменяемое и восстанавливаемое закрытие (выполнено 28.09.2026)

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

Реализовано: `DesktopShutdownCoordinator` объединяет параллельные запросы закрытия и
выполняет единственную последовательность `stop UI scenarios -> shutdown supervisor ->
ingest commit barrier -> session-end barrier`. `MainWindow.Closing` отменяет обычное
закрытие до успешного результата. При persistence failure окно остаётся открытым,
показывает явную ошибку и следующая попытка закрытия повторяет только незавершённые
durable операции, не перезапуская quiesce/supervisor shutdown.

`MessageIngestor.FlushAsync` теперь завершается ошибкой как для уже приостановленного
writer, так и при сбое, возникшем во время ожидания барьера; текущий DTO остаётся в
очереди до `RetryAsync`. Добавлен singleton `SessionCompletionTracker`, сохраняющий в
памяти неудавшиеся записи `Sessions.EndedUtc`/причины и повторяющий их по явному retry.
После успешных барьеров Avalonia выходит из main loop, затем `Program` закрывает DI,
SQLite и instance lock в прежнем порядке. При OS shutdown выполняется best-effort
финальный прогон без обещания отменить завершение системы; этот неизбежный риск
остаётся явно отделён от штатного закрытия.

Проверено: полный Release build без предупреждений; Core tests — 96/96, Desktop —
46/46, MeshCoreSharp regression suite — 92/92. Fake-тесты подтверждают точный порядок,
одну операцию для параллельных запросов, успешный/неуспешный flush, сохранение pending
message/session-end до retry и отсутствие повторного quiesce. Интеграционный тест с
временной SQLite и настоящим instance lock подтверждает, что после ошибки они остаются
доступны, а после успешного retry освобождаются; сохранённая история читается после
повторного открытия. Существующий barrier-тест подтверждает приём сообщения во время
session shutdown. Физическая нода не использовалась; schema и MeshCoreSharp/protocol
не изменялись.

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
Подробности — в [аппаратном отчёте](testing/messenger-b7-4-serial-2026-09-28.md).

Физический unplug/replug выполнен из устойчивого `Online`: разрыв перевёл supervisor
в `RetryWaiting`, неудачные открытия создали только последовательные generation с
backoff 1/2/4/8/15 секунд, а повторно подключённая нода вышла в `Online` в новой
session с тем же NodeId. Shutdown завершил session и durable barriers, pending ingest
равен нулю, порт освобождён. TCP не проверялся, поскольку endpoint не указан; этот
пункт был условным «по возможности» и не блокирует приёмку Serial-профиля.

Проверено: полный Release build без предупреждений; Core tests — 97/97, Desktop —
46/46, MeshCoreSharp regression suite — 92/92. **Этап B завершён 28.09.2026.**

##### B7.5 — отделение transport profile от identity ноды (выполнено 28.09.2026)

После финального аудита пересмотрено ранее принятое в B1/B2/B6 решение хранить
ожидаемый ключ ноды в connection profile. Оно делало нормальную замену устройства на
том же Serial/TCP endpoint ошибкой `NeedsAttention` и требовало ручной перепривязки.

Новая модель:

```text
ConnectionProfile = способ подключения и reconnect policy
Node              = identity по полному SelfInfo.PublicKey
Session           = конкретная попытка с ProfileId и обнаруженным NodeId
```

Из `ConnectionProfile` и application API удалены `ExpectedNodePublicKey`, операция
его обновления, mismatch-result/exception и `UseConnectedNodeAsync`. После `APP_START`
`CompanionSession` всегда ищет или создаёт Node строго по полному 32-byte ключу,
связывает с ним текущую session и продолжает directory load/drain. Ни endpoint,
ни имя профиля, ни display name ноды в identity не участвуют. Supervisor/reconnect
каждую generation выполняет Identify заново; ошибки БД/ingest и несовместимые ответы
по-прежнему переходят в `NeedsAttention`.

SQLite schema не менялась. Nullable-колонка `ConnectionProfiles.ExpectedNodePublicKey`
остаётся legacy артефактом ранней схемы: текущая domain-модель и store её не читают,
не записывают и не используют для решений. Тест с ранее заполненной колонкой
подтверждает открытие и обновление такого профиля без миграции или потери legacy
значения. `MeshCoreSharp` и Companion protocol не изменялись.

Детерминированные fake-тесты подтверждают первую идентификацию, один NodeId для
одного ключа через Serial/TCP и при смене display name, разные NodeId для разных
ключей через один профиль и полный reconnect-сценарий `A -> B -> A`. Production-like
harness с настоящими supervisor/session/coordinator/ingestor и SQLite сохраняет
12 сообщений в двух историях: возврат A продолжает историю A, B остаётся отдельно,
а late events закрытых sessions не попадают в активную историю. Database failure
сохраняет `NeedsAttention` без retry; lifecycle, barriers, commit-before-UI и
recoverable shutdown не изменены. Физическая вторая нода для проверки не требуется.

Проверено: полный Release build без предупреждений; Core tests — 98/98, Desktop —
46/46, MeshCoreSharp regression suite — 92/92; `git diff --check` чист.
**Этап B завершён с уточнённой моделью identity 28.09.2026.**

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

### Анализ исходного состояния Stage C (28.09.2026)

Checkpoint перед планированием: `1ee572a` (B7.5); после коммита дерево было чистым.
Последняя проверка реализации: Release без предупреждений, Core 98/98, Desktop
46/46, MeshCoreSharp 92/92. Эти числа — baseline Stage B, не результаты Stage C.

| Уже есть в A/B | Чего пока не хватает для C |
| --- | --- |
| `Nodes` по уникальному полному ключу; `Sessions` с ProfileId/NodeId; generation guard | Списка известных нод для offline UI и отдельного контекста просмотра истории |
| Snapshot supervisor с ProfileId, SessionId, NodeId и состоянием | Представления фактической ноды; сейчас заголовок привязан к редактируемому `Profiles.SelectedProfile`, а не к профилю текущей attempt |
| `ILocalHistoryReader`, SQLite read-only соединения и лимит страницы 500 | Node-фильтра списка диалогов, проверки принадлежности выбранного диалога ноде, курсора списка диалогов |
| Курсор сообщений `beforeLocalSequence`, индекс `(ConversationId, LocalSequence)` | Чтения после/вокруг позиции, UI подгрузки, ограниченного окна данных и сохранения scroll anchor |
| Двухпанельный XAML shell, ListBox, превью, локальное время, Connect/Disconnect | Вкладок, адаптации узкого окна, выбора/копирования текста, поиска и поведения фокуса |
| Сохранённые Contacts.ContactType/PresentOnNode, Channels.AccessKind, bindings и metadata сообщений | Read projections со сведениями о типах, актуальными именами и служебными сообщениями; текущий `ConversationSummary` читает только Conversations/Messages |
| Commit-driven reload, UI dispatcher, объединение сигналов, версии выбора | Обновления только нужного node/view context без сброса всей истории на последние 100 сообщений |
| `Conversations.LastReadSequence`, таблица/record Drafts, Settings | API и UI непрочитанного/черновиков, сохранения темы и geometry |
| Quiesce, ingest/session-end barriers, retry закрытия, instance lock | Durable flush новых UI-записей (особенно черновиков) до успешного выхода |

Основные точки расширения: Core `ILocalHistoryReader`/`SqliteLocalHistoryReader`,
`INodeStore`, read projections справочников, существующий `DatabaseWorker`;
Desktop `MainWindowViewModel`, Views, dispatcher и shutdown coordinator. По мере
добавления сценариев выделять небольшие ViewModels навигации/истории/черновика из
текущего shell. Новые проекты, второй writer и общий framework событий не нужны.

### Уточнения границ и порядка

Исходный checklist C сохраняется. Необходимые уточнения вследствие фактического кода:

- Контекст ноды и запросы с явным NodeId предшествуют вкладкам. Сохранённый профиль,
  профиль активной attempt, фактическая нода и просматриваемая offline-нода — разные
  понятия. «Устройства» означает служебные контакты выбранной собственной ноды,
  а не список собственных нод.
- До визуальной группировки нужны read projections: `ConversationKind.Contact`
  сам по себе не означает Chat, а `Title` у принятых сообщений может быть NULL.
  Для каналов использовать сохранённый AccessKind, не имя. `SharedSecret` может
  пока не встречаться в реальном snapshot: B3 определяет проверенный hashtag либо
  Unknown; классификацию/создание канала по действию пользователя оставляем D.
- Пагинацию Core и поведение viewport реализовать отдельно, до непрочитанного и
  поиска. Наличие ListBox и LIMIT в SQL ещё не доказывает ограниченность памяти UI.
- C включает редактирование и сохранение локального черновика. Отправка, Enter-to-send,
  валидация wire UTF-8 лимита, ACK и mutations остаются D. В C Enter вводит строку.
- Использовать существующие LastReadSequence/Drafts/Settings. Схему заранее не
  менять; необходимый индекс добавлять только отдельной новой миграцией после
  измерения SQL на fixture. Legacy ExpectedNodePublicKey не трогать.

### Общие контракты для C1–C10

- Источник фактического `ActiveNodeId` — текущий supervisor snapshot после Identify;
  до Identify он неизвестен. Имя/полный ключ для отображения читаются из Nodes по
  этому ID с проверкой версии snapshot. Наличие NodeId не означает Online:
  Synchronizing/NeedsAttention отображаются честно. Историческое имя не является
  доказательством текущего подключения.
- `ViewedNodeId` принадлежит Desktop-навигации. Offline startup восстанавливает
  последний корректный выбор из локальной БД; если выбора нет — показывает выбор
  ноды/пустое состояние, не выбирает её по профилю. Selector сохранённых нод
  доступен только в состоянии `Offline`. Как только Identify определил фактическую
  ноду и supervisor перешёл в `Synchronizing` (либо сразу в более позднее состояние),
  просмотр принудительно переходит к этой ноде; во время Connecting/Identifying,
  RetryWaiting/Disconnecting и NeedsAttention ручная смена node context запрещена.
  Reconnect снова определяет и выбирает фактическую ноду. Offline очищает active
  identity, сохраняет последний контекст и вновь разрешает выбор локальной истории.
- Каждый запрос/результат истории несёт node scope; принадлежность ConversationId
  этому NodeId проверяется в Core. UI context revision включает выбранную ноду,
  вкладку, диалог и запрос; она отделена от connection generation. Поздний результат
  чтения не меняет новый контекст. Сообщения прежней session сохраняются в её истории.
- UI перечитывает только committed SQLite. `IncomingMessageCommitEvent` сейчас
  несёт ConversationId/sequence, без NodeId: определить scope через read projection
  либо минимально дополнить post-commit DTO. Нельзя выводить его из active node.
  Справочники должны обновляться и после Online без новых сообщений; использовать
  завершение синхронизации/snapshot, не добавляя consumer `CompanionSession.Events`.
- Read metadata/черновики идут через существующий writer; успешный UI-статус только
  после commit. Все новые фоновые чтения/записи входят в lifecycle shutdown;
  отписки, cancellation и retry не могут терять принятые данные.
- Для каждого реализуемого подэтапа: targeted deterministic tests, полный Release
  build, Core/Desktop/MeshCoreSharp regression suites и `git diff --check`.
  Fake Companion, dispatcher, время и временная SQLite — основное доказательство.
  Нода/радиопередачи не нужны для C. Ручные проверки фиксировать как выполненные
  только после реального запуска; неизвестный результат не подменять unit-тестом.

### Рекомендуемый порядок

`C1 -> C2 -> C3 -> C4 -> C5 -> C6 -> C7 -> C8 -> C9 -> C10`.
Каждый подэтап — отдельная реализация и приёмка; следующий автоматически не начинать.
Имена будущих API ниже обозначают возможности, а не требование создать именно
такие классы. C1–C2 реализованы ниже; C3–C10 пока **не реализованы**.

#### C1 — контекст собственной ноды и node-scoped чтение

**Выполнено 29.09.2026.**

**Цель:** shell однозначно различает способ подключения, фактическую ноду и историю,
которую пользователь читает; последующие UI-сценарии получают безопасный scope.

**Scope:** bounded чтение списка Nodes; явный NodeId для списка диалогов и чтения
сообщений с проверкой принадлежности; минимальный selector ноды в существующем
shell; отображение профиля attempt по Snapshot.ProfileId отдельно от редактируемого
профиля; имя/ключ активной ноды; правила ViewedNodeId и восстановления выбора из
Settings, описанные выше. Защита async reads и commit reload при смене контекста.
Полный public key доступен в сведениях, сокращение в заголовке — только отображение.

**Не входит:** новые вкладки, расширенные projections, unread, drafts, новый дизайн,
изменение supervisor policy. **Зависимости:** завершённый B7.5.

**Обязательные тесты:** A -> B -> A на одном профиле, A через два профиля;
одноимённые ноды; чужой ConversationId отклоняется/не возвращает данные; медленный
ответ A после выбора B; commit старой session не попадает в B; изменение редактора
профиля не меняет подпись активной attempt; Offline/Identifying без ложной active
identity; startup без ноды и восстановление выбора после рестарта.

**Готово:** все видимые диалоги/сообщения shell принадлежат ViewedNodeId, а статус
отдельно показывает фактическое подключение; локальная история доступна до сети.
**Ручная UI-проверка:** да, selector/статус и offline startup на тестовой БД с A/B;
вторая физическая нода не требуется.

Фактическая реализация: `INodeStore` получил bounded список сохранённых Nodes;
`ILocalHistoryReader` требует явный `NodeId` и проверяет владельца `ConversationId`.
Commit DTO несёт сохранённый `NodeId`, поэтому Desktop игнорирует commit другой
ноды без вывода identity из текущего соединения. SQLite schema не менялась.

Shell хранит отдельные `ActiveNode` и `ViewedNode`, но не разрешает им расходиться
при установленной session identity. Active identity и подпись attempt-профиля
берутся только из supervisor snapshot; редактор профиля их не подменяет. Viewed node
восстанавливается из Settings и выбирается вручную только Offline. После Identify
состояние `Synchronizing` автоматически переводит просмотр к фактической ноде;
кнопка «К активной» и прежний follow-флаг больше не участвуют в поведении. Имя в
selector дополнено сокращённым ключом, полный 32-byte public key доступен в сведениях.
Смена node повышает отдельную revision чтения: поздний результат старой node не
применяется. Offline/Identifying очищают active identity, но сохраняют историю.

Детерминированные тесты покрывают A -> B -> A через один профиль, одну ноду через
два разных connection profile, одноимённые ноды, чужой ConversationId, позднее
чтение A после выбора B, commit старой ноды, независимость profile editor, отсутствие
ложной identity до Identify, пустой startup и восстановление выбора. Release build
прошёл без предупреждений; Core tests — 100/100, Desktop — 57/57, MeshCoreSharp —
92/92. Desktop-окно успешно запущено на отдельной временной SQLite fixture с A/B:
offline history загрузилась до event loop (`Загружено диалогов: 1`), подключения и
пользовательская БД не использовались. На физической Heltec V3 подтверждено Serial-
подключение через `/dev/cu.usbserial-0001`: UI показал Online, отдельные active profile
и node, selector, полный ключ и committed history. Визуальное A/B-переключение на
двух физических нодах не выполнялось и не требуется; оно доказано fake-тестами.
Disconnect/restart и reconnect в этой ручной сессии повторно не проверялись — их
гарантии остаются покрыты Stage B и deterministic C1 tests. Скриншот также подтвердил,
что C1 остаётся инженерным shell; навигация/компоновка запланированы в C3/C5/C9.
Подробности — в [ручном отчёте C1](testing/messenger-c1-ui-2026-09-29.md). На момент
фиксации C1 реализация C2 ещё не начиналась.

#### C2 — read projections диалогов и справочников

**Выполнено 29.09.2026.**

**Цель:** предоставить UI достаточные committed данные для корректных вкладок.

**Scope:** node-scoped страницы Chat/служебных контактов, каналов и unknown histories;
имена через join с сохранённым справочником, ContactType, PresentOnNode, AccessKind,
активность bindings и read-only карточки. Сохранять видимость истории отсутствующего
контакта/канала. Списки показывают и записи справочника без сообщений: stable key
такой строки не равен ConversationId (он может отсутствовать). Просмотр не создаёт
пустую историю. Два слота одного канала не дублируют чат. Стабильная сортировка с
tie-breaker и ограниченные cursor pages списков; пересортировка после commit должна
явно инвалидировать/обновлять страницу без дубликатов. Обновление после directory
sync, даже если message commit не было. Отдельные read DTO без channel secrets.
Карточка показывает только уже сохранённые сведения; отсутствующие поля маршрута
или адверта помечаются неизвестными, не восстанавливаются предположениями.

**Не входит:** XAML вкладок, mutations, автоматическое переразрешение старых unknown
сообщений, чтение новых сведений из ноды. **Зависимости:** C1.

**Обязательные тесты:** Chat/Repeater/Room/Sensor/None/unknown type; NULL Title и
rename; удалённый со справочника контакт с историей; unknown/ambiguous prefix;
одноимённые каналы с разными fingerprints, общий fingerprint в двух слотах;
Unknown не превращается в hashtag из-за `#` в имени; пустые справочники/истории,
страницы без пропусков на неизменном снимке и без дублей после refresh; изоляция A/B.

**Готово:** эти сценарии доступны через bounded Core read API, не требуют SQL в
ViewModel и не меняют stored identity. **Ручная UI-проверка:** не обязательна;
достаточно SQLite integration tests, визуальная приёмка следует в C3.

Фактически добавлен отдельный read-only `IConversationDirectoryReader` с пятью
node-scoped секциями: Chat, служебные контакты, каналы, unknown contacts и unknown
channels. Страница ограничена 200 строками; cursor несёт NodeId, section, activity
sequence/time и stable key, поэтому cursor другой ноды/секции отклоняется. Stable key
из полного contact public key, channel fingerprint или сохранённой unknown identity
не зависит от nullable ConversationId. Строки Contacts/Channels без переписки
видимы, но чтение не создаёт для них Conversations.

Known entries используют актуальные committed имена и metadata через join; удалённый
из текущего snapshot контакт с историей сохраняется с `PresentOnNode=false`.
Один channel fingerprint в нескольких active slots возвращается одной строкой со
списком слотов, одинаковые имена с разными fingerprints не сливаются. AccessKind
читается как сохранённый факт: имя с `#` не превращает Unknown в hashtag. Отдельные
карточки контакта/канала возвращают только сохранённые route/advert поля и fingerprint,
никогда channel secret. Last-message projection сохраняет ResolutionState для
различения unresolved/ambiguous histories.

Семь SQLite integration tests покрывают Chat/Repeater/Room/Sensor/None/unknown type,
NULL Title, rename, отсутствующий контакт с историей, unresolved/ambiguous prefix,
все AccessKind, одинаковые имена, общий fingerprint в двух slots, directory-only
entries, стабильные cursor pages, refresh без дублей, пустые данные, cancellation,
bounds и изоляцию A/B. Полный Release build прошёл без предупреждений; Core tests —
107/107, Desktop — 57/57, MeshCoreSharp — 92/92. SQLite schema, Desktop XAML,
supervisor и MeshCoreSharp не изменялись; ручная UI-проверка для C2 не требуется.

#### C3 — вкладки и read-only навигация

**Выполнено 30.09.2026.**

**Цель:** превратить shell в интерфейс чтения с различимыми группами адресатов.

**Scope:** «Личные», «Каналы», «Устройства» на projections C2; отдельные unknown
groups, фильтры по AccessKind, превью/время; карточки служебных контактов и их
read-only история. Пустые состояния, листание списков и сохранение выбора по
stable key при refresh. Двухпанельная компоновка, на узком окне «Назад»; восстановление
последней вкладки/диалога отдельно для каждой ноды. Минимальная декомпозиция shell
ViewModel по владению навигацией и историей.

**Не входит:** создание/удаление контактов/каналов, редактор черновика, расширенная
история сообщений. **Зависимости:** C1–C2.

**Обязательные тесты:** фильтры и выбор вкладки; служебные контакты не в «Личных»;
unknown доступны; entry без ConversationId имеет пустую историю; сохранение выбора
при rename/reorder, node switch во время загрузки; повторное открытие локальной БД;
directory-only sync обновляет список; все изменения коллекций через UI dispatcher.

**Готово:** всю сохранённую историю можно найти в правильной группе своей ноды;
read-only карточки не предлагают передачу. **Ручная UI-проверка:** да, широкое/узкое
окно, длинные имена, пустые группы и навигация клавиатурой на fixture всех типов.

Фактически плоская C1-навигация заменена отдельной
`ConversationNavigationViewModel`, которая читает только C2 projections. Реализованы
вкладки «Личные», «Каналы», «Устройства», отдельные unknown-группы, фильтр каналов
по committed `AccessKind`, previews/время, read-only metadata карточки и ограниченная
первая страница истории. Directory-only запись остаётся видимой с nullable
ConversationId и не вызывает чтение/создание пустого диалога. Редактор connection
profile вынесен из постоянной левой колонки в закрываемую панель настроек.

Последняя вкладка и stable key записи сохраняются в Settings отдельно для каждого
NodeId. Refresh после commit или завершившегося Online directory sync сохраняет
выбор при rename/reorder; context revision отбрасывает поздние результаты старой
ноды. Списки имеют cursor-команду «Показать ещё», а на ширине менее 760 px список
и детали переключаются явной кнопкой «Назад». C4 paging истории, C5 viewport,
unread, search и send UI не добавлялись.

Восемь новых deterministic Desktop tests покрывают разделение Chat/service/channel,
unknown-группы, AccessKind filter, directory-only запись, stable-key refresh,
node switch во время загрузки, node-scoped восстановление выбора, directory-only
sync refresh, UI dispatcher и narrow-layout back. Release build прошёл без
предупреждений; Core tests — 107/107, Desktop — 65/65, MeshCoreSharp — 92/92;
`git diff --check` чист. Повторное открытие реальной SQLite и recoverable shutdown
остаются покрыты существующими Core/Desktop regression tests.

30.09.2026 пользователь подтвердил ручную работу C3 на физически подключённой
Serial-ноде. На wide layout проверены вкладка каналов, список, выбор и committed
история; на narrow layout — отдельная карточка истории и явная кнопка «Назад».
Скриншоты подтверждают, что редактор профиля больше не занимает постоянную колонку.
Отправка и mutations не выполнялись и в C3 отсутствуют. Подробности — в
[ручном отчёте C3](testing/messenger-c3-ui-2026-09-30.md).

#### C4 — API страниц истории и адресуемой позиции

**Выполнено 30.09.2026.**

**Цель:** подготовить одно основание для scroll, first unread и результатов поиска.

**Scope:** дополнить bounded history API чтением до/после sequence и вокруг конкретного
сообщения, признаком наличия следующей страницы. Позиция включает NodeId,
ConversationId и устойчивый message id/LocalSequence. Сортировка по LocalSequence,
не по ошибочным wire timestamps. Добавить в read DTO только необходимые сведения
для текстовых/служебных/бинарных сообщений и отображения времени. Контракт позволяет
повторно прочитать выгруженный диапазон, не загружая всю историю. Проверить SQL/index
на 100 000 сообщений; новый индекс — только при подтверждённой необходимости.

**Не входит:** viewport Avalonia, unread writes, поиск и экспорт payload.
**Зависимости:** C1–C2; рекомендован после стабилизации навигации C3.

**Обязательные тесты:** начало/конец/середина, пустой диалог, пропуски глобального
LocalSequence между диалогами, одинаковые timestamps, concurrent append; соседние
страницы без дублей/потерь, неверная node/conversation позиция, cancellation;
room-post/TextType, binary и отсутствующий wire timestamp не выдаются за обычный
личный текст; fixture 100 000 сообщений с границей до 500 DTO на один запрос.

**Готово:** переход к произвольному сообщению требует только ограниченного набора
страниц; нет OFFSET-прохода с материализацией всей истории.
**Ручная UI-проверка:** не требуется, UI использует API с C5.

Фактически `ILocalHistoryReader` дополнен методами получения точной позиции по
MessageId, хронологических страниц before/after и bounded-окна around. Позиция
включает NodeId, ConversationId, MessageId и LocalSequence; чужой scope отклоняется,
а подменённая пара MessageId/LocalSequence не принимается. Каждая страница содержит
первую/последнюю позицию и независимые признаки `HasEarlier`/`HasLater`; старый
`GetMessagesAsync` сохранён для совместимости C1–C3.

`HistoryMessage` теперь также проецирует nullable TextType, BinaryDataType и
WireTimestamp, а также ResolutionState. Binary payload намеренно не попал в read DTO.
SQL использует keyset range по LocalSequence и возвращает данные хронологически;
timestamps не участвуют в сортировке. `EXPLAIN QUERY PLAN` на fixture из 100 000
сообщений подтвердил использование существующего
`IX_Messages_Conversation_Sequence`, поэтому SQLite schema не менялась.

Семь новых SQLite integration tests покрывают начало/конец/середину и пустой диалог,
глобальные gaps, одинаковые timestamps, соседние страницы, append между страницами,
wrong node/conversation и forged position, cancellation/bounds, TextType/binary/
missing wire timestamp, а также лимит 500 DTO на 100 000 сообщениях. Release build
прошёл без предупреждений; Core tests — 114/114, Desktop — 65/65, MeshCoreSharp —
92/92. Ручная проверка не требуется; C5 ещё не начинался.

#### C5 — ограниченное окно истории и поведение прокрутки

**Выполнено 30.09.2026, включая ручную UI-приёмку.**

**Цель:** читать длинный диалог и новые сообщения без скачков и роста памяти.

**Scope:** viewport с виртуализацией и отдельным ограничением числа DTO в памяти;
подгрузка/выгрузка страниц, сохранение anchor при prepend и commit, кнопка к последнему
сообщению. Автопрокрутка только если пользователь уже у конца; иначе индикатор новых
сообщений. Выделение/копирование текста, время, честные read-only placeholders
бинарных/служебных сообщений. Сообщать фактически видимый диапазон и активность
окна для C6; не считать загруженную страницу просмотренной. Вместо полного Clear/
reload последних 100 записей сохранять устойчивый контекст и обновлять нужный диапазон.
Начальный бюджет: страницы по 100 сообщений, до пяти страниц/500 message DTO в
активной истории; при выходе за окно страницы выгружаются и доступны для повторного
чтения. Любое изменение бюджета обосновать измерением и зафиксировать в тесте.

**Не входит:** unread persistence, поиск, rich media, delivery UI.
**Зависимости:** C3–C4.

**Обязательные тесты:** bounded коллекции при длинном scroll, повторная загрузка
выгруженной страницы; commit в конце/в середине/другой ноде; anchor не меняется при
prepend; late read после смены диалога; duplicate post-commit сигнал; copy Unicode;
отмена всех загрузок при shutdown. UI/контрольный тест подтверждает виртуализацию,
а тест ViewModel отдельно подтверждает предел данных в памяти.

**Готово:** 100 000 сообщений доступны страницами, число UI-объектов ограничено и
incoming не сбрасывает позицию чтения. **Ручная UI-проверка:** обязательна — scroll,
resize, выделение, multiline, новые сообщения при чтении середины и потеря фокуса.

Фактически добавлена отдельная `HistoryWindowViewModel`, использующая C4 pages по
100 сообщений и удерживающая максимум пять страниц / 500 DTO. Prepend сохраняет
anchor по LocalSequence и при переполнении выгружает дальний newer-край; append
симметрично выгружает older-край. Выгруженный диапазон повторно загружается без
дубликатов. Смена node/conversation имеет собственную revision, а shutdown отменяет
все незавершённые page reads.

Post-commit больше не очищает текущую историю. У фактического конца committed
сообщения добавляются и запрашивают автопрокрутку; при чтении середины остаётся
позиция, появляется дедуплицированный индикатор новых сообщений и команда перехода
к bounded latest page. Commits другой ноды/диалога окно не меняют. Viewport сообщает
первый/последний видимый LocalSequence и active/end state для будущего C6, но никаких
read writes пока нет.

XAML использует проверяемый `VirtualizingStackPanel` с ограниченным cache; текст
сообщения переведён на `SelectableTextBlock`. Unicode копируется без изменения,
а binary, CLI/signed text и room-post не маскируются под обычный личный текст.
Первая ручная проверка выявила, что производный `VirtualizedHistoryListBox` не
получал стандартный template `ListBox`: загруженные сообщения существовали во
ViewModel, но не создавали visual containers. Контролу явно назначен базовый
`ListBox` style key, а контрольный тест теперь проверяет и style key, и
`VirtualizingStackPanel`.
Вторая ручная проверка выявила race при входящем сообщении: обновление каталога
кратковременно снимало selection у `ListBox`, а asynchronous UI handler принимал
технический `null` за запрос очистить выбранный диалог. Null-selection от refresh
теперь игнорируется на UI-границе и закреплён отдельным regression test.
Девять новых deterministic Desktop tests покрывают лимит 500 при длинном scroll,
повторную загрузку выгруженной страницы, anchor prepend, commit у конца/в середине/
другой ноде, duplicate signal, late read после смены диалога, Unicode/placeholders,
shutdown cancellation, UI dispatcher, selection refresh policy и виртуализирующий
control. Release build прошёл без предупреждений; Core tests — 114/114, Desktop —
74/74, MeshCoreSharp — 92/92; `git diff --check` чист. C6 ещё не начинался.
Пользователь повторно проверил приложение с физической Serial-нодой после
исправлений: сообщения отображаются, выбранный диалог сохраняется при incoming,
поведение оценено как корректное. Ручная приёмка C5 завершена.

#### C6 — непрочитанное и переход к первому непрочитанному

**Выполнено 30.09.2026.**

**Цель:** сохранять честный read position независимо для каждого диалога/ноды.

**Scope:** Core read count/first unread и монотонная writer-операция LastReadSequence;
считать только подходящие incoming записи, обновлять UI после commit. Метка
прочитанного требует активного окна, открытого диалога и видимого диапазона C5.
Для одного watermark принять консервативное правило: продвигать только просмотренную
непрерывную границу непрочитанных; просмотр более позднего участка сам по себе не
погашает пропущенный непрочитанный промежуток. Переход к первому непрочитанному через
position API C4; read writes входят в shutdown lifecycle.

**Не входит:** радиоквитанции прочтения, системные уведомления, per-message read flags,
автоматическое «прочитать всё» при выборе диалога. **Зависимости:** C4–C5.

**Обязательные тесты:** неактивное/свёрнутое окно, другая вкладка/нода, только часть
страницы видима; непрочитанный gap и jump-to-latest; incoming во время read commit;
повтор/обратный watermark, outgoing не увеличивает count, рестарт, writer failure
не выдаёт ложный успех, shutdown дожидается принятых read writes.

**Готово:** unread сохраняется после рестарта и уменьшается только по принятому
правилу просмотра. **Ручная UI-проверка:** обязательна — фокус окна, скролл,
первое непрочитанное и поступление новых сообщений во время чтения.

Фактически добавлен `IConversationReadStateStore` поверх уже существующего
`Conversations.LastReadSequence`; SQLite schema не менялась. Read projection
node-scoped и содержит монотонный watermark, число только incoming после него и
точную `HistoryMessagePosition` первого непрочитанного. Writer проверяет точное
соответствие node/conversation/message/sequence, не допускает обратного движения и
возвращает состояние только после commit. Directory projection считает unread в
том же node scope; список диалогов показывает badge, ограниченный визуально `99+`.

C5 viewport теперь отбрасывает виртуализированные cache-containers вне видимой
области. `HistoryWindowViewModel` не пишет read state при открытии, загрузке страницы
или jump-to-latest. Продвижение разрешено только активному и видимому окну, когда
первый непрочитанный входит в фактический visible range; gap выше viewport остаётся
непрочитанным. Кнопка unread загружает bounded страницу вокруг первой непрочитанной
позиции и сохраняет её anchor. Incoming во время read commit остаётся unread до
фактического появления в видимом диапазоне; late completion старого контекста
игнорируется.

Первая ручная проверка C6 выявила остановку продвижения при непрерывном быстром
scroll: visible ranges, пришедшие пока предыдущий SQLite write был в полёте,
отбрасывались, после чего уже подтверждённый first-unread оказывался выше viewport
и корректная gap-защита не позволяла продолжить. ViewModel теперь накапливает
перекрывающиеся или соседние фактически просмотренные диапазоны до завершения write
и coalesce-ит их в более позднюю цель. Диапазон с реальным пропуском по-прежнему не
продвигает watermark. Сценарий закреплён deterministic regression test; повторная
ручная проверка подтвердила исправленное продвижение unread при прокрутке.

В ходе той же ручной проверки уточнена node-navigation policy: после Identify UI
автоматически открывает историю фактически подключённой ноды и блокирует selector
до полного перехода в Offline. Поэтому подключение больше не требует действия
«К активной», а reconnect не может оставить открытой историю другой ноды. Старое
значение Settings `desktop.follow-active-node` остаётся безвредным legacy и больше
не читается; схема SQLite не менялась. Тесты покрывают offline B -> synchronizing A,
запрет смены на B при соединении и повторное разрешение выбора после Offline.

`ConversationReadStateTracker` сериализует и coalesce-ит принятые цели разных UI
сигналов. Ошибка не уменьшает отображаемый unread, сохраняет pending target и
останавливает успешный shutdown; повторное закрытие выполняет retry без повторного
quiesce UI/connection. UI stop дожидается уже начатых задач, а общий shutdown имеет
отдельный read-state durable barrier после ingress/session barriers.

Пять новых Core tests проверяют реальный SQLite incoming-only count/first unread,
точный node scope, outgoing, repeat/reverse watermark, рестарт/directory projection,
serialization/coalescing и failure/retry tracker. Одиннадцать новых Desktop tests
проверяют inactive window, видимый gap и частичный диапазон, отсутствие записи от
page load/jump-to-latest, переход к first unread, writer failure, accepted-write
shutdown barrier/retry, incoming во время commit, late completion другой ноды,
и unread badge; существующий bootstrap test дополнительно проверяет DI wiring.
Release build прошёл без предупреждений; Core tests —
119/119, Desktop — 86/86, MeshCoreSharp — 92/92; `git diff --check` чист. C7 ещё не
начинался. В повторной ручной сессии подтверждены offline history, отображение
unread badge, уменьшение счётчика только при прокрутке просмотренного диапазона и
доступность selector нод после перехода в Offline; приложенный скриншот зафиксировал
18 непрочитанных в непросмотренном канале. После этой проверки пользователь принял
C6 к фиксации.

#### C7 — локальный поиск и переход к результату

**Цель:** находить диалог и сообщение без выгрузки всей истории в UI.

**Scope:** bounded поиск имён внутри выбранной ноды/вкладки; поиск текста внутри
выбранного диалога, постраничные результаты с message position, переход/подсветка
через C4–C5. Литеральный Unicode substring; явно зафиксировать регистрозависимое
сравнение первой версии, не обещать Unicode case folding от SQLite NOCASE.
Debounce на injectable time, отмена и проверка query revision. Запрос выполняется
вне UI; cancellation длительного SQL требует отдельной проверки, поскольку нынешний
DatabaseReader проверяет token только перед action, а LIMIT не ограничивает scan.

**Не входит:** FTS, поиск сразу по всем нодам, regex, semantic search, экспорт.
**Зависимости:** C2–C5; после C6, чтобы поиск не обходил правила read position.

**Обязательные тесты:** кириллица/emoji, literal `%`/`_`/кавычки, пустой запрос,
границы страниц, isolation, быстрые последовательные запросы и stale results;
результат вне загруженного окна, deleted/missing target, поиск на 100 000 записей,
отсутствие UI blocking и ложного mark-read по одному поисковому результату.

**Готово:** поиск bounded по результатам, отзывчив и приводит к правильному
сообщению в своей ноде. **Ручная UI-проверка:** да, быстрый ввод, отмена, фокус,
переход назад в историю и Cmd/Ctrl для поиска.

Фактически реализовано 01.10.2026. `ConnectionProfile`/active-node модель не
изменялась. В Core добавлены node/conversation-scoped API поиска directory и
текстовых сообщений: страницы ограничены 200 и 100 результатами соответственно,
desktop запрашивает по 100 имён и 20 сообщений. Сравнение — параметризованный
регистрозависимый literal Unicode `instr`; wildcard-семантики для `%`/`_` нет.
Каждый message result несёт точный `HistoryMessagePosition`; переход загружает
bounded окно C4 вокруг target, прокручивает к нему и подсвечивает, но не вызывает
read-state write. Missing target и read failures отображаются немодально.

Оба UI-поиска используют инъецируемую задержку 250 ms, cancellation и query
revision; late result старого запроса не применяется. `DatabaseReader` регистрирует
`sqlite3_interrupt` на token, поэтому отменяется и уже исполняющийся SQLite scan.
В окне добавлены поля поиска имён и текущей переписки, paged result list,
«Показать ещё» и Cmd/Ctrl+F с выбором поля по текущему layout/selection. FTS,
глобальный all-node поиск, regex, semantic search и schema changes не добавлялись.

Детерминированные тесты покрывают кириллицу/emoji, регистр, literal `%`, `_` и
кавычки, пустой запрос, pagination, node/conversation isolation, 100 000 сообщений,
прерывание активного SQLite query, injectable debounce, rapid query/stale result,
переход к сообщению вне текущего окна, deleted target и отсутствие ложного
mark-read. Release build прошёл без предупреждений; Core tests — 122/122, Desktop —
91/91, MeshCoreSharp — 92/92; `git diff --check` чист. Ручная UI-проверка C7 пока
не выполнена; до неё проверить быстрый ввод/отмену, Cmd/Ctrl+F, выбор результата,
подсветку и возврат к истории. C8 не начат.

#### C8 — локальные черновики и durable UI-записи

**Цель:** не терять набранный текст при навигации, рестарте и обычном закрытии.

**Scope:** Drafts API через существующий writer, небольшой редактор только у Chat/
разрешённого канала, debounce с версией текста и ownership `(NodeId, ConversationId)`;
очистка пустого draft. Для записи справочника без диалога — idempotent локальное
получение/создание Conversation по устойчивому identity при первом сохранении draft,
согласованное с конкурентным ingest. Отдельный retryable flush dirty drafts перед
успешным shutdown; quiesce UI нельзя превращать в отмену незаписанного текста.
После ошибки сохранять текст/revision в памяти и повторять запись при retry закрытия;
уже остановленные connection/ingest этапы не запускать повторно. IME не инициирует
действий приложения; Enter/Shift+Enter в C только редактируют текст.

**Не входит:** send/ACK, контакты/каналы mutations, draft неизвестному или служебному
адресату, attachments. **Зависимости:** C1–C3, C5; порядок после C7 уменьшает
одновременные изменения навигации и обработки клавиатуры.

**Обязательные тесты:** быстрый A-chat -> B-chat -> A-chat и A-node -> B-node;
late save не затирает новый текст; empty/delete; рестарт; создание conversation
одновременно с ingest; shutdown до debounce, сбой записи/повтор без потери текста,
параллельное закрытие и освобождение SQLite/lock только после всех durable barriers.

**Готово:** последний принятый редактором текст сохраняется при обычном закрытии
либо остаётся доступным для retry после явной ошибки. **Ручная UI-проверка:** да,
Unicode/IME, смена вкладок/нод во время ввода, закрытие сразу после последнего символа.

Фактически реализовано 01.10.2026 без изменения SQLite schema. `IDraftStore`
читает/пишет существующую таблицу `Drafts` по устойчивому owner из NodeId, kind и
полной contact/channel identity. Directory-only Chat или известный канал получает
Conversation только при первом непустом draft; операция идемпотентна в общем
`DatabaseWorker` и согласована с конкурентным ingest. Пустая строка удаляет draft,
пробелы и Unicode сохраняются без нормализации. Unknown и служебные записи в UI
остаются read-only; send/ACK и любые mutations ноды не добавлялись.

`DraftEditorViewModel` принимает каждое изменение в общий `DraftWriteTracker` до
debounce 500 ms, принудительно flush-ит прежнего owner при смене диалога/ноды и
защищён context revision от late load. Tracker хранит newest revision при ошибке и
не позволяет late save пометить более новый текст сохранённым. UI stop отменяет
только таймер; отдельный `IDurableDraftWrites` barrier добавлен после ingress,
session completion и read-state. Повтор shutdown выполняет draft retry, не повторяя
quiesce UI и connection shutdown; параллельные close разделяют одну attempt.

Детерминированные тесты покрывают A-chat -> B-chat -> A-chat, A-node -> B-node,
late load/save, empty/delete, restart, Unicode, directory-only creation одновременно
с ingest, shutdown до debounce, failure/retry без потери newest text, совместный
concurrent shutdown и ожидание draft barrier. Release build прошёл без предупреждений;
Core tests — 127/127, Desktop — 98/98, MeshCoreSharp — 92/92; `git diff --check`
чист. Ручная UI-проверка C8 пока не выполнена: проверить Unicode/IME, Enter и
Shift+Enter без отправки, быстрые смены диалогов/нод и закрытие сразу после ввода.
C9 не начат.

#### C9 — тема, геометрия окна и завершение keyboard UX

**Цель:** сделать уже работающие сценарии удобными при ежедневном запуске.

**Scope:** system/light/dark через Settings, сохранение normal bounds/window state,
восстановление окна в доступной области экрана при смене монитора/DPI; Cmd/Ctrl,
Tab/focus, Escape/Back для существующих сценариев, масштабирование и контраст.
Проверить постоянный статус ноды/профиля/retry и немодальные ошибки во всех layouts;
ошибку сохранения не стирать успешным reload истории. Записи настроек включить в
уже определённый порядок завершения; не задерживать startup сетевыми действиями.

**Не входит:** новый дизайн-системный framework, полная локализация, tray/notifications,
новые команды ноды и telemetry polling. **Зависимости:** C3, C5, C7–C8.

**Обязательные тесты:** persistence темы/geometry, fallback для повреждённых Settings,
недоступный монитор/невалидные bounds, сохранение normal bounds при maximized;
маршрутизация shortcuts при разных focus/IME, отсутствие send, ошибки writer и
shutdown. Платформенные вычисления отделить от реально запускаемого окна.

**Готово:** preferences переживают рестарт, окно доступно, основные действия
выполняются клавиатурой. **Ручная UI-проверка:** обязательна на macOS и, при наличии,
Windows — темы, DPI/мониторы, Cmd/Ctrl, IME. Недоступную платформу отметить как
непроверенную и перенести именно её платформенную приёмку в E.

**Фактически выполнено 01.10.2026.** Добавлен desktop-only `DesktopPreferences`
поверх существующей `Settings` без изменения схемы: system/light/dark и normal window
bounds/maximized сохраняются revisions и последним retryable barrier штатного
shutdown. Повреждённые значения безопасно дают system theme/штатную геометрию.
Чистый `WindowPlacementCalculator` отделяет validation/clamp/primary fallback от
Avalonia window и учитывает screen scaling при применении физических bounds. В окне
добавлены динамическое переключение темы, сохранение normal bounds при maximized,
Cmd/Ctrl+F, Escape и Alt+Left; text editor/IME не перехватываются, send отсутствует.
Статус profile/node/retry и немодальные ошибки остаются в постоянном header во всех
layouts. Settings читаются локально до показа окна; сетевой lifecycle по-прежнему
стартует только после `Opened`.

Детерминированно покрыты SQLite restart темы/геометрии, повреждённые Settings,
недоступный монитор, invalid/oversized bounds, normal bounds при maximized, routing
Cmd/Ctrl/focus/Escape/Back и защита text input/IME, failure/retry Settings writer и
точный shutdown order. Release build прошёл без предупреждений; Core — 127/127,
Desktop — 111/111, MeshCoreSharp — 92/92. Ручная проверка C9 на macOS ожидается:
system/light/dark, maximized и перенос/смена масштаба монитора, Cmd/Ctrl+F,
Escape/Alt+Left, Tab/focus и IME. Windows недоступна и её платформенная приёмка
переносится в E. C10 не начат.

#### Компонентная переработка UI перед C10 (согласована 02.10.2026)

Это уточнение первоначального оформления Stage C после C9, а не изменение истории
выполненных этапов. Обоснование: [анализ UI](UI_COMPONENTS_AND_REDESIGN_ANALYSIS.md).
Принят последовательный визуальный workflow с ручной проверкой после каждого шага:

1. **UI1 — NavigationShellView.** Две области: rail и content, три верхние вкладки
   «Публичные чаты», «Приватные чаты», «Устройства»; две нижние — «Подключение»,
   «Настройки». Иконка-заполнитель и центрированная переносимая подпись; справа
   название выбранного раздела. Готово после проверки всех пяти кнопок, единственного
   выбора, wide/narrow размеров и Tab/Space keyboard focus.
2. **UI2 — ChatWorkspaceView.** Общий контролл списка/истории/редактора и кнопки
   отправки для публичных и приватных чатов. Fake data для визуальной проверки;
   демонстрационная отправка не касается эфира/пользовательской БД. Реальная
   отправка остаётся D. Проверить длинный текст, viewport, wide/narrow и editor/IME.
3. **UI3 — DevicesWorkspaceView.** Список и read-only карточка на fake fixtures
   Repeater/Room/Sensor/unknown, nullable metadata, без remote commands. Проверить
   смену выбора, неполные данные и отсутствие чат-редактора.
4. **UI4 — ConnectionSettingsView.** Редизайн существующих Serial/TCP/timeout/
   AutoConnect/Reconnect полей и выбора профиля, отдельный контролл. Визуальная
   проверка валидации, разных транспортов и двух профилей; network integration — UI6.
5. **UI5 — ApplicationSettingsView.** Отдельный экран общих настроек, сейчас theme;
   подключение не содержит оформление. Проверить system/light/dark и доступность.
6. **UI6 — интеграция.** Подключить те же views к рабочим VM и durable services,
   проверить node ownership, selection/scroll/read/search/draft при переходах,
   callbacks/attach/detach, writer errors и shutdown. Обязательны fake A/B/A,
   invisible chat не погашает unread, restart и все regression suites; ручная
   проверка обязательна. После этого выполнить исходный C10.

Каждый шаг зависит от предыдущего принятого визуального checkpoint; production
Core/SQLite/protocol не переписываются ради компоновки. Fake preview следующих
контролов должен быть изолирован от радио и пользовательской истории.

**UI1 фактически реализован 02.10.2026.** `NavigationShellView` — UserControl на
обычных Avalonia Grid/ItemsControl/RadioButton/ContentControl, `NavigationShellViewModel`
владеет только shell selection. Navigation items разбиты на верхнюю и нижнюю группы;
квадратные placeholders и подписи, взаимно исключающий выбор, theme/focus styles.
Главное окно показывает shell вместо прежней монолитной workspace; реальные чат,
карточки и формы ещё не встроены и на всех вкладках ожидаемо стоят текстовые
заглушки. Постоянный connection/profile/node/error статус, window preferences и
recoverable shutdown сохранены. Старые history viewport/scroll/focus adapters убраны
из Window; они будут возвращены в соответствующий компонент, не в shell.
Существующий startup/AutoConnect policy сохранён; UI1 сам не вызывает connection
commands или send/advert/mutation и не сообщает просмотр скрытой истории.

Тесты UI1 проверяют порядок/начальный выбор, commands всех пяти разделов и одну
selection между двумя группами. Release build: 0 warnings/errors; Desktop 113/113,
Core 127/127, MeshCoreSharp 92/92. Ручная визуальная проверка UI1 ещё ожидается;
UI2–UI6 и C10 не начаты.

**Доработка панели пользователем и аудит 02.10.2026.** Квадратные placeholders
заменены отдельными PNG-иконками Channels/Private/Devices/Connection/Settings.
`ShellIconAssets` задаёт avares URI, `IconUriConverter` кеширует bitmap, а view
использует альфа-канал как OpacityMask с foreground brush текущей темы. Все шесть
PNG (включая сохранённый резервный placeholder) имеют RGBA 512×512 с прозрачными
и непрозрачными пикселями; каталог Assets включён в AvaloniaResource. Цвет пикселей
PNG не задаёт цвет иконки. Tooltip появляется справа через 400 ms с отступом 8.
Margin перенесён внутрь button template: прозрачная внешняя область остаётся частью
контрола, уменьшая разрыв между областями взаимодействия соседних вкладок.

Порядок вкладок, единый выбор между группами, подписи, независимость shell от node
context и текстовые заглушки справа сохранены. Production-код при аудите не менялся.
Release build: 0 warnings/errors; Desktop 113/113, Core 127/127, MeshCoreSharp 92/92;
`git diff --check` чист. Автотесты доказывают shell selection и прежние regressions,
а не фактический рендеринг PNG/tooltip: ручная проверка всех иконок в light/dark,
hover, Tab/Space и размеров окна остаётся отдельной приёмкой.

Неблокирующий технический долг: конвертер не закрывает явно поток AssetLoader.Open;
при следующей доработке загрузчика стоит использовать using. Bitmap cache сейчас
содержит только фиксированный набор иконок и используется view на UI thread;
динамическую загрузку/удаление произвольных ресурсов этот контракт не покрывает.

#### C10 — совместная приёмка Stage C

**Цель:** доказать совместимость подэтапов и отсутствие регрессий Stage B.

**Scope:** интеграционный сценарий на временной SQLite: offline restore -> подключение
fake A/B/A -> выбранная история/вкладка -> scroll/read/search/draft -> commit failure
и retry shutdown -> рестарт. Fixture с 100 000 сообщениями и несколькими нодами,
измерение пиковых размеров DTO/коллекций и SQL/UI задержек в Release; отчёт с ОС,
архитектурой и результатами в docs/testing. Исправлять только обнаруженные нарушения
критериев C, новые возможности выделять отдельно.

**Не входит:** hardware send/advert/mutation, упаковка/подпись и release-приёмка E.
**Зависимости:** C1–C9.

**Обязательные тесты:** весь интеграционный сценарий выше, отсутствие смешивания нод
и двух reconnect loops, late read/commit после смены контекста, bounded memory при
длительном scroll, commit-before-UI и полный набор shutdown regressions. Все три
regression suites и Release build обязательны.

**Готово:** исходный checklist C подтверждён тестами и ручным отчётом; ограничения
платформ перечислены явно. **Ручная UI-проверка:** обязательна, по сценарию C10;
для A/B достаточно fake data/session, второй физической ноды не требуется.

### Риски перед реализацией и ручной baseline перед C1

Главные риски переделки: смешение ActiveNodeId/ViewedNodeId (закрыть в C1), неверная
типизация ConversationKind и устаревшие Title (C2), обновление directory без message
commit (C2–C3), отсутствие cursor/anchor контракта (C4 до C5–C7), неограниченный рост
DTO несмотря на виртуализацию (C5), трактовка прочитанного через один watermark (C6),
debounced writes после необратимого StopAsync (C8). Обычный SQL substring может
сканировать длинную историю; проверить cancellation/latency в C7 до решения о FTS.
SQLite schema и старые миграции не переписывать ради упрощения UI.

Перед C1 вручную зафиксировать текущий baseline shell: offline запуск с пустой и
непустой тестовой БД; чтение/выбор диалога; узкое окно и длинный текст; смена выбора
в редакторе профиля и отличие от действующей attempt; закрытие и повторный запуск
без захвата lock. Использовать отдельный тестовый data directory без изменения
пользовательской БД; в текущем Program нет готового аргумента выбора каталога,
поэтому способ безопасного fixture-запуска надо определить до GUI-проверки (тестовый
bootstrap с IAppPaths или поддерживаемое перенаправление окружения). Не добавлять
новый product CLI только ради анализа. На этом шаге ручной baseline **не выполнялся**.

Функции Stage D (send, UTF-8 wire validation, ACK, управление контактами/каналами,
удаление и advert), F (FTS, системные уведомления, диагностика) и E (пакеты/полная
платформенная приёмка) остаются в своих этапах. Аудит и разбиение C не разрешают
их реализацию. Детали визуального оформления выбирать внутри C3/C5/C9, не вводя
заранее новые библиотеки или инфраструктуру.

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
| Другая нода по тому же адресу | Автоматически определяется другой NodeId; истории не смешиваются |
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
