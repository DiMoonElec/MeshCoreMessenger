# REGION — архитектура и общие контракты

[Этапы REGION](stage-region.md) · [Оглавление](../../MESSENGER_PLAN.md) · [Протокол и исследование ноды](../../library/protocol/regions.md)

16.10.2026: анализ и проектирование выполнены. Реализация **не начата**.
Названия новых типов/таблиц ниже — проект, не существующий API.
GUI проектируется совместно с пользователем: документ задаёт сценарии и данные,
но не утверждает экраны, кнопки, меню и оформление.

## Результат этапа

Приложение умеет вести локальный каталог регионов, находить доступные имена через
репитеры, назначать scope каналу, читать/изменять default ноды и корректно отправлять
с нужным scope. Управляемые приложением отправки, повторы и ЛС защищены от
пересечения override; reconnect и смена ноды не переиспользуют старое состояние.
Входящие сообщения, история, ACK, SQLite barriers и транспортные контракты сохраняются.
Неподдерживаемые возможности объясняются отдельно и не ломают обычный приём.

Полнота относится к Companion-сценариям, доступным прошивке. Администрирование
RegionMap удалённых репитеров, GPS-геозоны, радиочастотные presets, синхронизация
настроек с закрытым мобильным приложением и недоступный firmware private keystore
не объявляются реализованными частями REGION. Ограничения и причины разобраны
в [исследовании](../../library/protocol/regions.md).

## Текущее состояние и точки расширения

| Уже существует | Что требуется REGION |
| --- | --- |
| `CommandType`: 54, 55, 57, 63, 64; `PacketType`: 1C, 8C, 8E | Encoders, typed packets/parsers, публичные методы и capabilities; enum недостаточно |
| `MeshCoreClient`, `CommandDispatcher`, `PacketRouter`, `AckTracker` | Scope-aware составная отправка и deferred discovery collector |
| `ChannelSecrets.DeriveHashtag` | Отдельный RegionKeys helper с region-правилами и byte limits; не подменять канал регионом |
| `BleTextBudget` с консервативным `regionScoped=true` | Рассчитывать по выбранной операции/снимку scope; неизвестный scope остаётся консервативным |
| `ICompanionClient` / `MeshCoreClientFactory` | Типизированные операции регионов без raw-команд в Core |
| `CompanionSession`, `SessionCommandGateway`, `SessionCommandLease` | Capabilities/default snapshot; generation-bound region-команды и admission |
| `DirectoryService`, `ContactRouteService`, `ConversationOperationGuard` | Координация discovery с изменениями контакта/пути; существующий lease не является async mutex всей отправки |
| `MessageService`, `PrivateDeliveryCoordinator`, канальные повторы | Неизменяемый scope каждой попытки; один общий wire путь |
| SQLite `DatabaseMigrator`, текущая версия 7, `DatabaseWorker` | Новая добавочная миграция, region store/read projections; номер выбрать по состоянию при реализации |
| Desktop composer, карточка канала, история, settings | Позже совместно утвердить представление; сначала закончить Core-контракты |

Базовые проекты, DI bootstrap, BLE/TCP/Serial и историю не пересоздавать.
`MeshCoreSharp.dll` остаётся единственной проектной Companion-сборкой.

## Владение состоянием и модель

Нода — источник истины для постоянного default. Приложение — источник истины
для каталога и назначений каналам. Каждое наблюдение default имеет SessionId,
generation, timestamp и freshness; offline отображается последнее наблюдение,
а не обещание текущего состояния устройства. Connect/reconnect читает данные,
но не восстанавливает автоматически default, каталог на репитерах или отправки.

Предлагаемые типы:

- `RegionDefinition`: локальный id, display label, точное protocol name,
  public/custom-key kind, key16, fingerprint, provenance и timestamps.
- `FloodScopeSelection`: **InheritDefault / ExplicitRegion / ForceUnscoped**.
  Unknown — состояние наблюдения/совместимости, не четвёртая пользовательская
  команда. Пустая ссылка на регион не означает unscoped.
- `NodeFloodScopeSnapshot`: Supported/Unsupported/Unknown, имя, key16 либо
  KnownEmpty, freshness и session identity.
- `RegionObservation`: имя, полный ключ репитера, LocalNodeId, время, источник,
  SNR/RSSI при наличии и состояние запроса. Отдельно от сохранённого пользователем
  каталога: discovery не удаляет и не заменяет настройки.
- `ResolvedSendScope`: policy revision, mode, фактический key fingerprint,
  snapshot имени/default, session identity, transport text budget и причина
  неизвестности. Снимок копируется до постановки отправки в работу.

Каталог и назначения ограничены `LocalNodeId`, определяемым полным ключом ноды,
а не профилем/портом. Одна нода через разные транспорты использует те же настройки.
Назначение канала привязывается к `(LocalNodeId, channel-secret fingerprint)`:
переименование и другой слот с тем же секретом сохраняют назначение. Замена секрета
создаёт другое назначение с InheritDefault; архивная история остаётся прежней.
Scope не входит в identity канала и не меняет ChannelBindingGeneration.

Один канал имеет одно предпочтение scope в приложении даже при нескольких слотах.
При необходимости разовой отправки в иной регион контракт может принимать override
операции; отдельные чаты для одного секрета по регионам создавать нельзя, поскольку
входящий wire не позволяет достоверно разделить такую историю.

Для публичного региона `#name` и `name` нормализуются только по одному служебному
префиксу. Case сохраняется. Label можно менять независимо; изменение protocol name
меняет ключ и создаёт новую definition, не переписывая историю отправок.
Для обычных публичных имён — совместимый с RegionMap алфавит, без NUL/CSV-разделителя,
непустое имя, не `*`, не `$...`, не повторный `#`. Лимиты проверять по UTF-8 bytes
на каждом wire endpoint; default name занимает максимум 30 bytes с учётом реально
передаваемого префикса. Не обрезать. Custom key ровно16, all-zero запрещён как
ExplicitRegion, потому что firmware интерпретирует его как fallback к default.
Не вычислять public key для неизвестного `$` региона из discovery.

## Хранение и локальные операции

Добавочные сущности SQLite, окончательные имена уточняются при реализации:

| Таблица | Ключ / содержание |
| --- | --- |
| `Regions` | Id, NodeId, kind, protocol name, label, key16, fingerprint, timestamps, archived; node-scoped unique identity |
| `ChannelRegionPreferences` | NodeId + ChannelId, mode, nullable RegionId, revision; CHECK соответствия mode/reference, FK без случайного каскадного сброса |
| `NodeRegionSnapshots` | NodeId, последнее подтверждённое default, SessionId, generation, observed time; cache, не очередь записи на ноду |
| `RegionObservations` | NodeId + repeater full key + exact name, first/last seen; ограничение объёма и срок хранения наблюдений |
| `SendAttemptScopes` | AttemptId, requested mode, policy revision, name/fingerprint snapshot, effective-known flag; не FK, меняющий старую историю при удалении каталога |

Хранить key material только там, где нужен для последующей отправки; custom keys
не выводить в diagnostics/logs. Публичные region keys не являются секретом канала.
Для воспроизводимых повторов нужен защищаемый приложением immutable key snapshot
или ссылка на неизменяемую key version, которую нельзя удалить до завершения
всех использующих её попыток. Одного fingerprint для отправки недостаточно.

Существующим каналам миграция задаёт InheritDefault без отправки команд устройству.
Исторические попытки получают LegacyUnknown: не вычислять им scope задним числом.
Никаких массовых изменений истории или IncomingMessageEvents.
Проверить migration rollback, FK, backup, schema-too-new и данные всех transport
профилей. Записи проходят через DatabaseWorker; UI обновляется после commit.

Операции `RegionService` (новый Core owner): список/добавление/переименование label,
архивирование, назначение каналу, refresh/default mutation, запуск/отмена discovery.
Изменение списка и предпочтения доступно offline, без отложенной передачи.
Изменение default ноды — только online через session gateway с последующим GET.
При timeout показывать неопределённость и перечитать, а не повторять SET автоматически.
При несовпадении readback хранить фактическое состояние и отдельный результат ошибки.

Удаление используемого региона не должно незаметно расширять охват до unscoped:
либо отказ со списком назначений, либо явная транзакционная замена назначений
на выбранную политику. Архив не удаляет snapshots истории и не меняет default ноды.
Обнаруженные имена с одинаковым ключом объединяются в каталог, но источники
наблюдений сохраняются; отсутствие имени в очередном ответе не удаляет его.

## Библиотека: безопасная отправка

Публичное направление API (имена предварительные):

```csharp
GetDefaultFloodScopeAsync(ct)
SetDefaultFloodScopeAsync(definition, ct)
ClearDefaultFloodScopeAsync(ct)
SendChannelTextAsync(slot, text, timestamp, scope, ct)
SendTextAsync(publicKey, text, timestamp, attempt, scope, ct)
DiscoverRepeaterNeighborsAsync(options, ct)
RequestRegionsAsync(repeater, replyPath, ct)
```

Не делать обычный GUI-путь из двух независимых публичных вызовов SetScope и Send.
Команда 54 — деталь составной операции. Существующие overloads на поддерживаемой
firmware должны входить в тот же механизм с InheritDefault, иначе legacy private
send сможет попасть между установкой region и канальной отправкой.

Предлагается внутренний `ScopeOperationGate`, общий для всех операций, которые
читают/меняют временный scope или передают через него, и для SET/CLEAR default.
Он не заменяет CommandGate и не удерживается до ACK или remote response:

```text
admission, target validation, immutable scope snapshot, durable attempt
  -> acquire ScopeOperationGate
  -> SET_FLOOD_SCOPE(mode/key), дождаться OK
  -> SEND, дождаться локального OK или MSG_SENT
  -> RESET override к InheritDefault, дождаться OK
  -> release ScopeOperationGate
ACK/deferred completion наблюдаются независимо
```

CommandGate берётся внутри на каждую immediate транзакцию; безопасные чтения
и drain могут идти между ними, другая scope-зависимая передача — нет.
RX/push и event queue работают непрерывно. Gate должен быть в библиотеке:
блокировки только в MessageService не защищают других пользователей public API.
Нужна матрица всех существующих/добавляемых transmit API; raw transmission при
добавлении не должна обходить определённый контракт незаметно.

ACK capacity и private-send admission ожидаются **до** ScopeOperationGate.
Для remote requests порядок: MeshRequestGate → ScopeOperationGate → CommandGate;
после immediate acceptance scope gate освобождается, remote gate остаётся до
terminal response. Не захватывать gates в обратном порядке и не ждать GUI/SQLite
в RX hook. Изменение default сериализуется со scope-зависимой отправкой, чтобы
InheritDefault не менялся между resolution и передачей.

При доступном свежем default InheritDefault разрешается в конкретный snapshot
key/empty внутри согласованной операции; библиотека может временно отправить
явный snapshot key для точности результата и повторов. Если default прочитать
нельзя, effective scope остаётся Unknown; не обещать воспроизводимость и увеличенный
unscoped budget. Пользовательский ExplicitRegion никогда не деградирует молча
в InheritDefault/Unscoped при unsupported command.

### Ошибки, отмена, восстановление

- До установки scope отмена не отправляет текст. Ошибка SET не допускает SEND.
- После подтверждённого SET и до SEND выполнить ограниченный cleanup, если
  канал command/response ещё достоверен; отменённый пользовательский token
  не должен немедленно отменять этот cleanup. Session stop прекращает его.
- Timeout/cancellation после возможной передачи immediate-команды создаёт риск
  позднего нетегированного OK. Не отправлять RESET/следующий SEND, который может
  принять старый OK. Закрыть command admission этой сессии и инициировать её
  завершение через существующий lifecycle; новая сессия не воспроизводит отправку.
- После принятого SEND ошибка RESET означает «отправка принята, scope cleanup
  не подтверждён», а не разрешение отправить текст повторно. Сохранить результат
  SEND отдельно от ошибки состояния устройства; ACK-tracker живёт по прежним
  правилам и может подтвердить доставку до закрытия соединения.
- После reconnect временный override считать неизвестным. Не сбрасывать его
  самим фактом connect: первая явно запрошенная отправка устанавливает свой
  mode/key независимо от прошлого RAM-состояния. Default читается без записи.
- Старые callbacks, discovery и readback не публикуются в новой generation.
  Shutdown закрывает admission, отменяет и ждёт принадлежащие ему работы/записи.

Firmware не обеспечивает транзакции между клиентами или crash-safe restoration.
Автоматические flood PATH/ACK в ответ на входящее ЛС также используют глобальный
override ноды. Gate приложения не предотвращает такой ответ в промежутке
SET→SEND→RESET: абсолютной изоляции автономных ответов здесь нет. Проверить это
на firmware/hardware отдельно; исправление требует firmware-поддержки scope
конкретной передачи или независимого scope автоматических ответов.
Основание: [уточнение про ЛС и ACK](../../library/protocol/regions.md#уточнение-лс-ack-и-асимметричные-маршруты).
Если приложение аварийно завершится после SET, override может остаться на ноде.
Другие приложения должны устанавливать собственный scope; одновременное управление
одной нодой не поддерживается. Эти пределы не устраняются записью в SQLite.

## Отправки, история, повторы и лимиты

`MessageService` разрешает region policy вместе с текущей channel binding и
session owner. Scope snapshot сохраняется до передачи, привязывается к попытке,
а не к редактируемой настройке канала. Изменение выбора после admission не меняет
уже принятую в работу попытку. Scope-ошибка до SEND отличима от возможной отправки,
иначе существующее правило Unknown ошибочно предложит повтор неотправленного текста.

Рекомендуемый контракт повторов: автоматические попытки одной логической отправки
сохраняют исходный resolved scope; ручной «повтор» тоже использует snapshot,
а «отправить как новое» разрешает текущие настройки. При LegacyUnknown или
утраченном key snapshot точный повтор запрещён, доступна новая отправка.
Выбор пользовательских формулировок и UX этого различия остаётся совместным.
Изменение default во время private retry не должно менять охват незаметно.
Private direct работает как раньше; flood fallback получает scope той же операции.
Базовый application-сценарий ЛС — default ноды; отдельная настройка региона
контакта не утверждена. Explicit scope в библиотечном API — техническая возможность,
не требование повторить несуществующее per-contact поле штатного приложения.
Не вводить новый retry/replay механизм и не менять количество уже разрешённых попыток.

В истории показывать выбранный/эффективный scope исходящей попытки только при
наличии данных; incoming scope = Unknown. Local OK канала не означает доставку
региону или получателям. ACK ЛС подтверждает сообщение, не географию доставки.

Budget вычислять из UTF-8 текста и sender prefix, transport constraints, radio
overhead и resolved scope. На первом шаге сохранить существующие консервативные
BLE пределы; увеличивать unscoped limit только при подтверждённом состоянии.
Текущий gateway `GetTextByteLimit(nodeId, isChannel)` недостаточен для разных
region preferences: добавить recipient/policy revision либо запрос готового
send-admission snapshot. Composer и отправка используют одинаковую оценку,
но последняя повторно проверяет её после ожидания gates. Без silent trim.
Пределы TCP/Serial не менять попутно без отдельного доказанного расчёта.

## Discovery в приложении

`RegionDiscoveryService` координирует явный ограниченный поиск, используя типизированные
методы библиотеки. Не создавать второй consumer `CompanionSession.Events`.
Deferred matching живёт в библиотеке; Core получает результаты и прогресс через
существующий session/generation ownership.

1. Собрать zero-hop соседей с полным public key; объединить с выбранными известными
   репитерами, для которых есть пригодный direct path. Dedupe по full key.
2. Применить preflight контакта/маршрута. Нельзя молча переписать flood path.
   Неизвестный сосед может вызвать firmware addContact; результат должен явно
   учитывать это действие и дальнейший refresh справочника.
3. Запрашивать репитеры последовательно под общим MeshRequestGate, с конечными
   deadline, ограничением количества и без бесконечного повтора rate-limited запросов.
4. Сохранять per-repeater outcomes: SuccessEmpty, SuccessPossiblyPartial,
   NoDirectPath, Unsupported, Timeout, Cancelled, Malformed, ContactTableFull.
   Timeout не означает «регионы не поддерживаются»; пустой CSV не означает ошибку.
5. Строго разобрать UTF-8/CSV, ограничить размер, сохранить неизвестные варианты
   диагностически, не вычислять фиктивные ключи. Частные имена — обнаружены,
   но непригодны к отправке без предоставленного ключа.
6. Discovery не выбирает регион автоматически. Пользователь сохраняет нужный
   результат в каталог либо вводит публичное имя вручную.

Для полного сценария «известный сосед с flood path» нужна отдельно обозначенная
операция временного direct route: snapshot полного контакта, эксклюзивное владение
его route на время запроса, изменение с readback, ограниченное восстановление
после terminal response. Не восстанавливать поверх более нового advert/route:
при конфликте обновить Directory и сообщить неопределённость. Ошибка восстановления
не скрывается как успешный read-only discovery. Реализация зависит от готовности
contact mutation/route ownership; D10 нельзя объявить выполненным заодно.
Без этого пути первый релиз discovery обязан честно возвращать NoDirectPath,
а REGION целиком не закрывается до согласованного полного сценария.

## Совместимость

Capabilities различать по функциям: TemporaryScope, ForceUnscoped, DefaultReadWrite,
NeighborDiscovery, AnonymousRegions. Protocol version — подсказка; подтверждённый
UNSUPPORTED_CMD отключает конкретную функцию в данной session. Неизвестную сборку
не считать поддерживающей всё по строке `1.18`. Read-only GET/default probe не
должен прерывать sync/history при штатном unsupported; timeout обрабатывается
с учётом небезопасного позднего ответа, а не маскируется как unsupported.

Без региона старые устройства продолжают прежние операции. Недоступные region
операции возвращают typed Unsupported до передачи, где это можно определить.
На устройстве без default API не выдавать Unknown за пустой default. Не использовать
local CLI как скрытый fallback с другими побочными эффектами.

## Сквозные критерии приёмки

Исполняемый порядок и небольшие задания вынесены в [REGION1–REGION34](stage-region.md).
Они заменяют первоначальные десять крупных блоков: реализация ещё не начиналась,
поэтому номера незавершённых работ переназначены без сохранения старых aliases.
Этот документ хранит контракты; фактические результаты записываются в файл шага.

Обязательные дополнительные regression-сценарии:

- Два канала с разными scopes, unscoped при непустом default, InheritDefault после
  explicit scope, private flood между канальными sends; точная wire последовательность.
- `MessagesWaiting`, ADVERT и ACK между всеми immediate ответами; initial drain
  и SQLite ingest продолжаются во время discovery и ожидания remote ответа.
- Входящее ЛС во время temporary channel override: отдельно проверить scope
  автоматически созданного firmware PATH/ACK; fake gate test сам по себе не
  доказывает изоляцию такого ответа. Direct исходное ЛС + flood ACK при неизвестном
  обратном пути; разные default scopes отправителя и получателя.
- Команда принята, cleanup не подтверждён; новый send не принимает старый OK;
  в БД нет ложного Failed для уже принятого SEND и нет автоматического replay.
- Повтор того же текста с тем же timestamp сохраняет прежнюю политику scope;
  смена region не создаёт новую channel identity и не сбрасывает unread/draft.
- Дублированные/запоздалые discovery ответы, full-key/prefix mismatch, пустой/длинный
  CSV, отсутствие NUL, invalid UTF-8, несколько источников одного региона,
  `$` без ключа, ограничение контактов, изменение маршрута во время запроса.
- Фактический default изменён другим клиентом между сессиями; каталог и назначения
  сохраняются, нода не переписывается при подключении.

Аппаратная матрица: зафиксировать firmware hash и protocol; проверить GET/SET/clear
default с восстановлением исходного значения и reboot persistence; получить
список от согласованного репитера; подтвердить scoped/unscoped передачу по
диагностике принимающей стороны, не только локальному OK. Проверить канал A→B→ЛС,
disconnect/reconnect, остановку посередине операции и перенос настроек между
TCP/Serial/BLE одной ноды. Передачи/мутации — в отдельно согласованном тестовом
сценарии с известными исходными настройками, после fake/regression проверок.

## Что обсудить по GUI отдельно

Нужны пользовательские сценарии: каталог и ручное добавление; поиск с источником
и неполнотой результата; три режима scope канала; default именно ноды; различие
локального сохранения и изменения устройства; состояния offline/unsupported/unknown;
отображение фактического scope исходящей попытки; удаление используемого региона;
объяснение побочных действий discovery и результата восстановления маршрута.

Открыты расположение этих действий, названия, форма выбора, показ в карточке/чате,
разовые overrides и детализация истории. Реализацию этих решений начать после
совместного обсуждения. До этого расширять Core/API и проверять их можно без
выдуманного GUI-контракта.
