# D4 — admission команд и session ownership

[Stage D](stage-d.md) · **Статус: реализован и автоматически проверен.**

**Цель:** send/mutations используют готовую session и безопасно завершаются.

**Scope:** маленькая Core command facade/lease и typed adapters library API,
расширение ICompanionClient для fake testing. Admission только Online, immutable
NodeId/SessionId/generation; закрыть admission до teardown, cancellation/tracked
completion/barriers, durable failure/retry. Supervisor остаётся lifecycle owner;
control loop не ждёт ACK. Observers не принадлежат UI. Нет replay; новая attempt
ждёт закрытия старой. Контракт применим к D10/D13/D17.

**Не входит:** user send, contact/channel workflows, второй event consumer,
raw client ViewModel. Runtime кнопки пока выключены.
**Зависимости:** D3; connections/shutdown, ConnectionAttempt/Session/ReceiveCoordinator.

**Тесты:** fake blocked command → disconnect/switch/shutdown во всех фазах; одна attempt;
stale lease до вызова → zero TX, после вызова → старое ownership;
receive/event/ingest barriers; shutdown write failure/retry; late completion
не меняет новую session; нет admission в Connecting/Synchronizing/RetryWaiting.

**Готово:** lifecycle boundary доказан до реального TX; изменение прежних state
publication/retry правил допускается только при необходимом обосновании.
**Ручная проверка:** не обязательна, основное доказательство — deterministic fake tests.

## Реализация 03.10.2026

- `SessionCommandGateway` выдаёт `SessionCommandLease` только для открытой Online
  session и запрошенной NodeId. Owner содержит неизменяемые NodeId/SessionId/generation
  и настоящее имя SelfInfo. Targets: node, полный ключ контакта, fingerprint с точной
  channel binding либо явно выбранный slot для будущих mutations. Memory inputs копируются.
- Supervisor открывает admission после полного synchronize/drain и закрывает перед
  публикацией любого не-Online состояния/teardown. Fault клиента, receive failure и
  outgoing persistence failure также закрывают admission. Старую scope нельзя открыть
  повторно; новая ждёт окончания старых операций и checkpoint сохранения.
- Invoke и закрытие admission используют одну короткую синхронную секцию. В ней не
  ожидаются async command results/ACK. Cancellation callbacks выполняются через
  CancelAsync вне supervisor control loop; даже исключение callback не оставляет
  leases без cleanup. Cancellation token остаётся доступен после disposal lease.
- `RunAsync` регистрирует весь Core workflow до начала; `ObserveAsync` учитывает
  дополнительные observers. **Caller D5/D6 должен включать принятие результата и
  его запись в owned workflow**, а не продолжать их в неучтённой UI-задаче после
  завершения вызова API. Scope закрывает idle leases и ждёт owned work/observers.
  Очередь событий по-прежнему читает только ReceiveCoordinator.
- ICompanionClient и production adapter получили typed send/contact/channel/advert
  методы публичной библиотеки. Старые read-only fake adapters имеют явный
  NotSupported fallback, не фиктивный успех. Lease не экспортирует client.
  GetContacts/GetChannels допускают будущий явный readback; никаких автоматических
  mutations/readback/adverts/replay в gateway нет. Полные channel secrets живут
  только в памяти конкретного вызова SetChannel.
- Text API требует binding к реальному Prepared attempt этой node/session/recipient.
  Store получил read-only `GetAsync` для immutable transmission capture; схема v3
  не менялась. Вызов использует сохранённый TransmissionText, без повторного
  processing и без произвольного аргумента text. Legacy записи без capture не
  отправляются через lease. Один attempt не может принадлежать двум leases.
- Перед text API обязательны committed Sending и повторная проверка contact Chat/
  полного ключа/однозначного prefix либо fingerprint/binding ID/generation/slot.
  Общий validator проверяет текущий UTF-8 бюджет настоящего имени session.
  На один attempt разрешён один text API invocation. Binding target проверяется
  также перед Clear/Set; slot-only mutation требует отдельного workflow D12/D13.
- Общие правила переходов вынесены в `SendAttemptTransitions`, используются store
  и lease. Accepted требует actual send invocation. Accepted/Delivery semantics
  по-прежнему определяет MessageService D5/D6. Timestamp writes монотонны внутри lease.
- Cleanup сохраняет Prepared; Sending без invocation получает Failed с доказанным
  zero TX; после invocation незавершённый Sending/AwaitingAck получает Unknown.
  Известные terminal результаты не меняются. Late owned completion пишет под
  старым owner до барьера; callback после завершения lease отклоняется.
- `OutgoingAttemptWriteTracker` удерживает failed writes в очереди вместе с ACK
  metadata и логическим порядком. Failed Accepted остаётся впереди cleanup Unknown.
  Ошибка записи или Prepare внутри owned workflow запрещает новые команды и даёт
  NeedsAttention. Retry сохраняет только БД. Factory flush выполняется до создания
  новой persisted session/client. Явный ConnectNow разрешает повтор сохранения;
  обычный reconnect/switch не снимает persistence pause.
- Session stop: close admission → quiesce drain → Disconnect/stop RX → дождаться
  owned work/status writes → library event barrier → unsubscribe/Dispose/session end;
  затем ReceiveCoordinator заканчивает event consumer и incoming commit barrier.
  Outgoing checkpoint включён также в ConnectionAttempt и Desktop shutdown. Ошибки
  outgoing persistence доминируют над transient cleanup errors. Shutdown не разрешает
  выход до outgoing Flush; следующая попытка shutdown вызывает Retry без TX.
- Исправлены два lifecycle edge cases, обнаруженные при fake проверках: ожидание
  Disconnect завершается после закрытия старой attempt даже если последующая команда
  изменила teardown intent на Restart; ConnectNow после failed factory без созданной
  attempt не вызывает Stop на null. Отменённый startup с pending teardown intent
  не открывает admission по позднему Online. Новых StateChanged publication points
  и изменений reconnect backoff нет. Root VM не расширена; изменены DI/shutdown wiring.

## Проверки

Deterministic fake client + настоящий supervisor/session/coordinator + временная SQLite:
нет admission до Online (Connecting/Identify/directory/drain), WrongNode/RetryWaiting;
idle stale lease zero TX после disconnect/reconnect; blocked API → disconnect/switch/
shutdown без замены старой attempt до owned completion; cancellation callback не
блокирует control loop; throwing callback сохраняет cleanup; Prepared/Sending/invoked/
AwaitingAck teardown; cancellation во время заблокированного SQLite Sending commit;
один text invocation/immutable stored text; wrong recipient/двойной attempt ownership;
retired binding и target copies; typed contact/channel mutations; failed Prepare и
Accepted, сохранение ACK metadata/Unknown в правильном порядке, no replay после
явного recovery; owned observer и late completion; incoming при Disconnect сохраняется
до замены session. Desktop: outgoing flush failure/retry и caller cancellation при flush.

Радио, пользовательская отправка, contact/channel workflows и UI wiring не подключены.
Ручная аппаратная проверка для D4 не требуется; реальные Send/ACK сценарии — D5/D6.

Debug/Release solution builds: 0 warnings/errors. Regression suites в обеих
конфигурациях: библиотека 96/96, Core 180/180, Desktop 267/267. C10 нагрузочный
сценарий сохранён: 100 000 сообщений, 1994 pages, DTO peak 512; Release SQL page
p50/p95 0,32/0,41 ms, restart+stop 28,28 ms. `git diff --check` и относительные
ссылки проверены. Native UI layout не менялся; отдельный визуальный preview D4
не требуется. Следующий подэтап — D5.
