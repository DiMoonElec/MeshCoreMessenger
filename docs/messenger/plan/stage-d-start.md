# Stage D: исходное состояние и контракты

[Порядок D1–D18](stage-d.md) · [Sending](../architecture/sending.md)

03.10.2026: анализ актуального кода; **ни один подэтап D не реализован**.
Сохраняются итоговые A/B/C и независимые Public/Private chat workspace.

## Что уже есть

- В `MeshCoreClient`: `SendTextAsync`, `SendChannelTextAsync`, `TextMessageSendResult.Delivery`,
  typed advert, contact/channel mutation и чтение справочников. Протокол заново не писать.
- `ProtocolLimits.MaxTextBytes=160`; кодировщик учитывает sender prefix канала, но
  reusable helper валидации пока internal. [Лимиты](../../testing/known-ui-limitations.md).
- В SQLite есть `Messages.Direction`, `SendAttempts`, enums/records, но нет outgoing
  store/service. `LocalStorage` предоставляет incoming/history/read/draft/directory stores.
- History читает Direction, unread SQL считает только Incoming. Статусов попыток
  в `HistoryMessage` нет; post-commit refresh статуса без нового сообщения ещё не реализован.
- `ComposerView` связан с `DraftEditorViewModel`; Send безусловно выключен. Draft/search/
  bounded history, copy исходного текста и rounded mentions должны сохраниться.
- `ICompanionClient`/session не предоставляют TX/mutation методы. `IConnectionAttempt`
  и supervisor — lifecycle only; raw client в UI недоступен.
- Session публикует типизированный `AdvertisementReceived`, но единственный
  `ReceiveCoordinator` consumer пока обрабатывает сообщения, MessagesWaiting и barriers,
  не discovery. `DirectoryService` читает полный snapshot, а не выполняет mutations.
- Channel bindings имеют отложенные переходы initial drain; действующего сценария
  ручной замены секрета во время Online нет. Полные секреты в БД не сохраняются.

Исходники: `Core/Application/{ConnectionSupervisor,ConnectionAttempt,CompanionSession,
IMeshCoreClientFactory,ReceiveCoordinator,DirectoryService}.cs`,
`Core/Domain/{StorageEnums,StorageRecords,HistoryModels}.cs`,
`Core/Persistence/{LocalStorage,Sqlite/DatabaseMigrator}.cs`,
`Desktop/Views/Chat/`, `Desktop/ViewModels/{ChatWorkspacesViewModel,DraftEditorViewModel}.cs`.
Пути относительно `src/MeshCoreMessenger.*`; это маршрут, не требование читать всё сразу.

## UI-first без подмены реальности

Для каждой группы сначала UI, затем настоящая функция. Preview использует те же
production controls/styles и явный демонстрационный presentation source в отдельном
dev/test harness. `mc-fake` — данные для чтения, не fake Online и не разрешение на радио.
Mock-команды не меняют настоящую SQLite, не создают session/attempt, не отправляют эфир.
В обычном запуске неподключённые действия неактивны и имеют понятное объяснение.
Не показывать выдуманный Delivered в реальной истории; demo маркирован явно.

Preview должен воспроизводимо открывать необходимые состояния, с командой запуска
в отчёте подэтапа. Не требуется новый глобальный dev-переключатель или новый профиль.
Один небольшой общий preview host допустим; не создавать параллельный UI/навигацию.
После wiring удалить заглушки из runtime пути, оставить их как тестовые adapters.

UserControls — в существующих Views/Chat, Views/Devices, Views/Connection либо
небольших feature-папках; `x:DataType`, локальный StyleInclude, Light/Dark токены.
Не новый монолит MainWindowViewModel: композиция services и маленьких feature owners.
Прирост root VM объяснять в отчёте; draft API не превращать во владельца радио.

## Решения до первой передачи

1. **Durable Sending отсутствует в схеме.** Enum сейчас 0–5: Prepared/Accepted/
   Delivered/Unconfirmed/Failed/Unknown; SQL CHECK допускает только этот диапазон.
   Нельзя трактовать Prepared как «вызов мог начаться»: это уничтожит crash distinction.
   В D3 рекомендуемый минимальный вариант — добавить Sending новым значением без
   перенумерации 0–5, нумерованной миграцией расширить CHECK. До реализации проверить
   backup/rebuild/FK/index совместимость; не редактировать старую миграцию.
   Accepted можно проектировать как private AwaitingAck либо завершённый AcceptedByNode
   по типу адресата/metadata, не обещая ACK каналу. Точное отображение фиксируется D3.
2. **Admission/lease команды.** В D4 получить immutable NodeId/SessionId/generation/
   recipient или slot binding; перепроверка перед wire. Не отдавать UI клиент или
   изменяемую «текущую session». Supervisor loop не ждёт radio ACK; нельзя блокировать
   Disconnect/Shutdown ожиданием UI-команды. Teardown закрывает admission, отменяет
   работу, ждёт owned observers и durable completion до новой attempt.
3. **Первые реальные send уже безопасны после аварии.** D3 делает startup recovery;
   D4–D6 — Unknown при неопределённом результате, flush/retry при сбое записи.
   D7 добавляет пользовательский повтор, не откладывает базовую сохранность.
4. **Черновик.** Capture отправляемый текст/адресат/revision при клике. Очищать только
   совпавшую revision после durable передачи ответственности message store; новый
   набранный текст или другой workspace не стирать. Отказ до prepare оставляет draft.
5. **Мутация не атомарна с БД.** Ошибка/тайм-аут после wire — неопределённый результат,
   не скрытый rollback/retry; readback/resync после явного действия. Сбой persistence
   запрещает новые передачи, требует NeedsAttention/durable retry, не reconnect loop.

## Общие инварианты и проверки

- Profile — только транспорт. Node identity — полный public key в каждой session.
  Одна attempt; late callbacks не меняют новую, старые committed данные не теряются.
- Единственный consumer Events — ReceiveCoordinator; discovery/drain transitions
  добавляются в его маршрутизацию. Ни UI, ни command gateway не читают channel.
- Prepared → Sending **до wire**, commit-before-UI для сообщений/статусов/справочника.
  Одно явное действие — один вызов Send/Set/Clear/Advert без скрытого повтора;
  необходимые readback/drain — отдельные безопасные чтения, не повторы мутации.
  Нет автоматической отправки после reconnect,
  startup, открытия формы; нет авто-advert или авто-добавления обнаруженных контактов.
- Владельцы ACK/операций живут в Core, не в выбранном чате. Private result Accepted
  сохраняется до Delivery даже при мгновенном ACK. ACK timeout не вызывает reconnect.
- Unknown/Unconfirmed не означают «точно не доставлено». Повтор предупреждает
  о возможном дубликате и создаёт новую попытку, не новый произвольный recipient.
- Core stores, fake client/session/time/dispatcher и временная настоящая SQLite —
  основное доказательство. UI-preview не заменяет сценарные тесты.
- Каждый UI-подэтап: Debug/Release build без предупреждений, Desktop tests,
  ресурсы/bindings, Light/Dark narrow/wide, клавиатура и Unicode.
  Каждый функциональный: также все Core/MeshCoreSharp regression suites и shutdown
  tests. Реальные runners из test projects: `dotnet test` ранее находил 0.
- После подэтапа `git diff --check`, фактический отчёт в его файле и остановка.
  Commit/push — только по текущему запросу. Не отмечать ручную проверку без пользователя.

## Вне D

BLE, radio configuration, remote telemetry/CLI, вложения, automatic retries,
новый text layout engine, фильтры/unknown redesign, шифрование БД, packaging не входят.
Приёмка на двух физических нодах остаётся отдельным требованием: если второй нет,
записать «не проверено», не заменять это заявлением об аппаратном успехе.
