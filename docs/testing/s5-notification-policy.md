# S5 — общий сервис и политика уведомлений о сообщениях

[План S5](../messenger/plan/s5-notification-policy.md) · [Ветка S](../messenger/plan/stage-s.md)

07.10.2026: **S5 реализован. Системный показ и click activation остаются S6.**
Пользователь согласовал общий сервис, принимающий запросы разных источников,
и отдельный координатор алгоритма уведомлений о сообщениях.

[Краткое описание архитектуры со схемой](../messenger/architecture/notifications.md).

## Владельцы и контракт

`IDesktopNotificationService.Submit(NotificationRequest)` принимает заголовок, тело,
необязательную типизированную цель и ключ объединения/замены. Цели сейчас — открыть
приложение или конкретное сообщение по NodeId/ConversationId/MessageId. Запрос не
содержит executable callbacks, команд подключения, ключей или launch arguments.
Другой источник может сразу отправить готовое уведомление, не применяя правила сообщений.
Новые источники событий в этом этапе не подключаются.

`DesktopNotificationService` владеет transient delivery queue и адаптером. До 128
ожидающих ключей, повтор ключа заменяет queued request; сверх лимита сохраняется
последний overflow request с исходным типом/политикой. Поток платформы не блокирует
Submit/ingest. Delivery ограничен 5 s и общим cancellation; adapter обязан проверять
токен до фактического показа. Ошибки логируются только по типу, без exception text.

`MessageNotificationCoordinator` подписывается на post-commit источник, учитывает
Inserted=true и объединяет сообщения одной переписки в фиксированном окне 500 ms.
Callback принимает только IDs/category/reception context, без SQL/UI/native calls.
Ограничение — 128 групп и два overflow accumulator для личных/канальных сообщений.
Перегруженный live burst превращается в одну сводку; initial batch — в одну сводку
после успешного завершения синхронизации. Pending initial batch не блокирует live группы.

`MessageNotificationPolicy` — специфический для сообщений helper координатора,
подключённый к общей delivery queue через `INotificationRequestPolicy`. Непосредственно
перед adapter он перечитывает exact message projection, настройки и фактическую
видимость; сообщения, ожидавшие за медленным адаптером, не используют прежний snapshot.
Остальные источники проходят через него без message rules. Generic service не знает
про переписки, SQLite, чекбоксы или ConnectionSupervisor; DI не образует цикл.

## Данные и правила

В Core добавлены `IMessageDetailsReader`/`CommittedMessageDetails` и node-scoped SQLite
read по трём IDs. Контактное имя берётся из Contacts, канальное — из Channels.LastName;
preview относится к exact message, а не последнему сообщению переписки. Нет binary
payload или секретных identity bytes; новая схема/миграция не нужны.

Incoming envelope сохраняет in-memory `IncomingSynchronization`. Commit event передаёт
SessionId, ReceivedUtc, private/channel category и тот же context, включая delayed
write/retry. После существующих drain/binding barriers CompanionSession ставит local
reception marker в прежнюю event queue. Единственный ReceiveCoordinator consumer
переключает фазу и ставит marker в ingress FIFO; completion следует за commit всех
предыдущих входящих. Ошибка ожидания использует прежний ingest retry, abort отменяет
сводку. Нет нового RX consumer, изменений wire protocol или алгоритма reconnect.

Два S2 checkbox применяются к известным и unknown перепискам. Исходящие, status/ACK
и Inserted=false не уведомляют. Binary, CLI/unknown text subtype имеют нейтральный
preview. Обычный/signed text нормализуется: control/bidi formatting убирается, переносы
заменяются пробелами, длина ограничена по grapheme clusters; emoji ZWJ сохраняется.
Unknown title не берётся из произвольного сохранённого display name.

`DesktopNotificationVisibility` читает состояние через UI dispatcher: окно visible,
active и не minimized, chat workspace действительно открыт, нет modal overlay,
node/conversation совпадают, история содержит exact message и viewport покрывает
весь burst range. Одного выбранного чата/IsAtLatest недостаточно. Notification не
изменяет unread и не выполняет connect/send/mutation. Summary возвращает приложение,
single-conversation request содержит exact message target для будущего S6.

App запускает coordinator до connection startup. `NotificationDesktopUiLifetime`
в начале настоящего shutdown отменяет delivery и pending groups, затем делегирует
прежний Root.StopAsync и существующую ошибку сохранения. Hide/Show их не останавливает.
Нет durable notification queue, чтения старой истории при старте или replay после restart.

## Проверки

- Debug/Release builds без warnings/errors.
- Release Core **387/387**; Desktop **385/385** в Debug и Release. Новые проверки: exact read scope,
  контекст после storage retry и порядок commit/boundary, Initial→Live с одинаково
  старыми node timestamps; четыре checkbox combinations, unknown/binary/subtype,
  safe preview/emoji, actual visibility, outgoing/deleted/read failure, поздняя смена
  checkbox, burst/dedup, initial completion/abort, другой NodeId, bounded overload
  обеих категорий, slow/throwing adapter, queued replacement, stop/restart без replay.
- Native macOS `--notifications-only`: настоящий MainWindow и временная SQLite,
  fake adapter. Active/actually-visible подавляет; hidden/inactive/scrolled/minimized
  показывает request с правильными name/preview/IDs. Incoming при скрытом окне не
  продвигает read cursor; ConnectCalls=0.
- Первый Core прогон в sandbox упал на запрете loopback bind у existing TCP fixtures;
  вне sandbox полный набор прошёл. В первом Desktop прогоне старый private-resend UI
  test упал на конкурентном Dictionary enumeration с ImmediateUiDispatcher;
  повтор полного suite прошёл. Эти старые компоненты не менялись.

```sh
dotnet build tools/MeshCoreMessenger.ViewportAudit -c Release --no-restore
dotnet tools/MeshCoreMessenger.ViewportAudit/bin/Release/net10.0/MeshCoreMessenger.ViewportAudit.dll --notifications-only
```

## Границы приёмки

В приложении зарегистрирован `UnavailableNotificationAdapter`: баннеры ОС пока не
показываются, Settings hint о недоступности остаётся до S6. Native audit проверяет
реальную видимость/SQLite/policy, а не macOS UserNotifications. Windows native
уведомления, разрешения, click/cold start и формат поставки проверяются в S6/S7.
Companion library не менялась; её suite в S5 повторно не запускался.

Сводка считает принятые новые записи, а не общее непрочитанное в БД. При перегрузке
детальные IDs некоторых сообщений заменяются счётчиком и последним представителем
категории. Видимость одного overflow представителя не подавляет сводку всего accumulator.
Частичный просмотр/удаление внутри большой группы не даёт точного числа
оставшихся непросмотренных; при смене node/batch сверх лимита старый overflow может
быть заменён. Это best-effort notification state: история не теряется. Существующая
радиодедупликация также не гарантирует распознавание всех повторов эфирного сообщения.
Нативный adapter, игнорирующий cancellation, может завершить показ поздно — S6 должен
соблюдать контракт, одного WaitAsync для предотвращения такого баннера недостаточно.
