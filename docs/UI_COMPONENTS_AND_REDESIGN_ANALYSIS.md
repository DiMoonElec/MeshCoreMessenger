# Анализ компонентов UI и новой компоновки MeshCoreMessenger

Дата: 02.10.2026. Основание: код после коммита `dfb9648` (C9),
`MESSENGER_ARCHITECTURE.md`, `MESSENGER_PLAN.md` и пользовательское описание.
Это предложение к плану: реализация редизайна и изменение основного плана на этом
шаге не выполнялись. Ручная приёмка C7–C9 и совместная приёмка C10 ещё ожидаются.

## Вывод

Предложенная компоновка подходит текущей архитектуре. Сложность средняя для
выделения views и замены навигации; основная сложность — корректный жизненный цикл
видимого чата, сохранение viewport и черновика при переходах и адаптация клавиатуры.
Переписывать Core, supervisor, протокол или SQLite-схему для этого не требуется.

Рекомендуемое место — завершающие UI-подэтапы Stage C после C9, до C10 и Stage D.
Границы C3/C5/C9 уже включают компоновку и оформление; предлагаемое изменение
уточняет их результат. C10 затем проверит окончательный UI, а отправку и mutations
в D можно будет добавлять в выделенные компоненты. Откладывание до E увеличит
стоимость переноса send/ACK/подтверждений и потребует повторной приёмки интерфейса.

Не стоит объединять выделение компонентов, замену навигации и визуальную полировку
в одну задачу. Рекомендуются четыре небольших последовательных подэтапа ниже.

## Что уже есть и где сосредоточена сложность

| Текущая часть | Подготовленность и проблема |
| --- | --- |
| `Views/MainWindow.axaml` — 359 строк | В одном файле header, selector собственной ноды, вкладки, справочник, поиск, история, draft и overlay настроек |
| `Views/MainWindow.axaml.cs` — 458 строк | Вместе window shutdown/geometry/theme, выбор записей, keyboard routing, scroll/anchor и read visibility |
| `MainWindowViewModel` — 821 строка | Владеет active/viewed node, connection presentation, обновлениями committed projections и координацией; подходит для shell, но содержит toggle overlay настроек |
| `ConversationNavigationViewModel` — 1229 строк | Уже разделяет Personal/Channels/Devices и node-scoped выбор; одновременно владеет справочниками, карточкой, history и draft |
| `HistoryWindowViewModel` — 1149 строк | Готовое bounded окно до 500 сообщений, search, scroll requests, unread и чтение; переносить поведение заново не нужно |
| `DraftEditorViewModel` и `DraftWriteTracker` | Готовые revision, debounce, ownership и durable shutdown; визуальное отделение редактора безопасно при сохранении владельца |
| `ConnectionProfilesViewModel` — 427 строк | Уже самостоятельный редактор Serial/TCP/профилей; его view пока встроен в главное окно |
| `IConversationDirectoryReader` | Разделяет ChatContacts/ServiceContacts/Channels/unknown; отдаёт secret-free contact/channel details |
| `DesktopPreferences`, `DesktopShortcutRouter` | Тема, placement и shortcuts уже выделены; маршрутизация пока предполагает поля конкретного MainWindow |

Размеры приведены как ориентир концентрации ответственности, а не как причина
механического разбиения классов. В первую очередь нужно разделить views и владение
визуальным lifecycle; большой рефакторинг всех ViewModel для первого шага избыточен.

Конкретные UI-проблемы, которые стоит закрыть вместе с компоновкой:

- Header содержит длинный title, статус и три кнопки в горизонтальной группе с
  `Auto`-шириной. При минимальной ширине 560 он конкурирует за место с названием;
  narrow logic сейчас меняет только панели переписки.
- `ConnectionStatusDetail` существует в ViewModel, но не отображается в XAML.
  Новая статусная область должна показывать причину/retry и различать выбранный
  профиль редактора и профиль активной attempt.
- Ошибки navigation/search/read/draft имеют разных владельцев. Новый shell должен
  отобразить их в подходящих областях, включая глобальную ошибку shutdown.
- Devices используют общий detail с заголовком и read-only историей. Отдельная
  полноценная карточка устройства пока отсутствует, хотя metadata уже читаются.
- Overlay настроек сейчас оставляет чат под ним. После замены на полноценный экран
  нужно явно сообщать, что история больше не просматривается.

## Предлагаемая компоновка

Под «чатами» в первой верхней кнопке понимаются каналы MeshCore; следующая кнопка —
личные Chat-переписки. Это сохраняет существующие разделы Channels/Personal/Devices.
Room остаётся устройством с read-only записями: интерактивный room-client не входит
в эту работу.

```text
┌──────────┬─────────────────────────────────────────────────────┐
│ Каналы   │ Постоянный компактный статус: соединение / нода      │
│ Личные   │ и активный профиль; баннер ошибки при необходимости │
│ Устройства├──────────────────────┬──────────────────────────────┤
│          │ Список каналов/чатов  │ Заголовок выбранного чата    │
│          │ Поиск, unread         │ История / поиск / draft      │
│          │                       │                              │
│ Настройки│                       │                              │
└──────────┴───────────────────────┴──────────────────────────────┘
```

Левая полоса шириной ориентировочно 56–72 логических пикселя остаётся постоянной.
Наверху — Channels, Personal, Devices; снизу — ConnectionSettings. Для расширения
достаточно описания navigation items и явного mapping section → view. Не нужен
plugin framework или универсальный сервис маршрутов.

Для Channels/Personal правая область содержит один общий `ChatWorkspaceView`
(список слева, выбранная переписка справа), настроенный на нужный раздел.
Devices показывает `DevicesWorkspaceView` со списком и карточкой; Settings занимает
правую область целиком. Настройки — вложенный UserControl в основном окне;
отдельное OS-окно здесь не требуется и создаёт ненужные вопросы focus/закрытия.

На узкой ширине rail остаётся, а chat/device workspace показывает либо список,
либо detail с «Назад». Breakpoint вычисляется по доступной ширине workspace после
rail, а не по прежнему `Window.Width < 760`. Статус перестраивается в несколько
строк; длинные имена сокращаются с tooltip/copy полного ключа.

Selector собственной ноды находится в общей контекстной области shell: доступен
только Offline, после Identify следует фактически подключённой ноде. Его нельзя
смешивать со списком устройств этой ноды или выбором connection profile.

Иконки — небольшие векторные Path/Geometry с tooltip, accessible name, видимым
выбором и focus indicator; emoji как основная навигация нежелательны из-за различий
рендеринга. Telegram служит ориентиром плотности и структуры: собственные цвета,
иконки и ресурсы, согласованные с system/light/dark.

## Границы компонентов и владельцы

| Компонент | Ответственность | Владелец данных/состояния |
| --- | --- | --- |
| `MainWindow` | Window placement, activation, close/shutdown, composition shell | Существующие preferences/shutdown services |
| `NavigationRailView` | Выбор верхнего раздела, кнопка Settings снизу | Shell section, независимый от выбранного диалога |
| `ConnectionStatusView` / node context | Всегда видимый connection/profile/node/error, Offline selector | `MainWindowViewModel`, snapshot supervisor |
| `ChatWorkspaceView` | Chat directory, adaptive list/detail, фокус поиска | Существующий navigation coordinator |
| `ConversationDirectoryView` | Список, фильтр каналов, unknown группы, selection events | `ConversationNavigationViewModel` на первом шаге |
| `ChatView` | Header, history search, viewport/scroll adapter, draft | `HistoryWindowViewModel` + `DraftEditorViewModel` |
| `ConnectionSettingsView` | Редактор профилей, port refresh, connect/disconnect, theme | `ConnectionProfilesViewModel` и явные shell commands/preferences |
| `DevicesWorkspaceView` | Node-scoped service directory, выбор устройства | Существующий directory API; отдельный devices VM при необходимости |
| `DeviceDetailsView` | Read-only metadata и связанные служебные записи | `ContactDetailsProjection`, immutable context полного ключа/NodeId |

Для Repeater/Room/Sensor сначала достаточно одного `DeviceDetailsView` с
условными блоками по типу. Отдельные специализированные карточки имеют смысл,
когда действительно появятся разные сценарии. Из текущих committed данных доступны
имя, тип, ключ, flags, OutPath, LastAdvertUtc, координаты и PresentOnNode.
PresentOnNode означает наличие в справочнике, а не доказанный online. Батарея,
текущий RSSI и удалённая телеметрия не подразумеваются этими projections.

UserControl не должен читать БД, создавать client/supervisor или подписываться на
session events. Бизнес-команды идут через ViewModel; code-behind допустим для
viewport, focus, translating positions и platform window events.

На первом шаге сохраняется один navigation/history/draft coordinator и его
существующие post-commit подписки. ViewModel живёт до shutdown приложения, а view
может скрываться или переподключаться. `StopAsync` не вызывается при переключении
вкладки: он необратимо останавливает workers. Заново создавать coordinator на каждый
выбор иконки нельзя. Скрытые views при этом не должны сообщать просмотр сообщений.

Для небольшой фиксированной навигации можно использовать явные DataTemplates в
ContentControl с сохранёнными VM, либо сохранённые views с переключением видимости.
Выбор сделать в UI1/UI2 по lifecycle тестам. В обоих вариантах контракты attach,
detach и visibility должны быть явными; кешировать неограниченное число чат-views
или историю каждого открытого диалога не требуется.

## Главные риски и обязательные инварианты

1. **Ложное чтение скрытого чата.** До ухода в Settings/Devices сообщить history
   inactive/невидимый viewport. Повторно сообщить актуальную видимость после
   layout при возвращении. Прежний `_isWindowActive` не должен оставаться true;
   новые commits в скрытом чате не погашают unread.
2. **Потеря scroll/anchor.** Пересозданный visual list теряет offset; queued scroll
   request может попасть в уже скрытый view. Сохранять stable sequence и offset,
   ограничивать callbacks текущим контекстом, возвращаться к anchor после layout.
   Сохранять существующий предел 500 DTO, auto-scroll только у фактического конца.
3. **Разная семантика screen и conversation.** Settings не является четвёртым
   `MessengerNavigationTab`, принадлежащим ноде. Shell section и последняя
   node-scoped вкладка/запись — разные состояния. Возврат из Settings сохраняет
   диалог, search и draft; переключение Channels/Personal использует существующий
   node-scoped контракт, не обещая новый кеш всех переписок.
4. **Черновик.** Уход с экрана не теряет dirty revision и не меняет его owner;
   смена NodeId/conversation по-прежнему выполняет безопасный flush. Connect из
   Settings может определить другую ноду: прежний draft остаётся в прежней истории.
5. **Подписки и shutdown.** Attach/detach не дублируют scroll/activation/commit
   handlers. Закрытие на любой странице сохраняет ingress/session/read/draft/
   preferences в прежнем порядке. Скрытые VM также quiesce при полном shutdown.
6. **Ownership данных.** ProfileId описывает транспорт, NodeId определяется по
   полному ключу каждой session. Все lists/details/search/drafts node-scoped;
   поздние результаты A не меняют B. UI продолжает обновляться после commit.
7. **Keyboard и доступность.** Cmd/Ctrl+F адресуется видимому workspace, не скрытому
   TextBox. Escape возвращает из Settings на прежний screen; Back в narrow сначала
   возвращает к списку. Draft/IME owns editing keys; Enter не отправляет до D.
8. **Errors и секреты.** Shell показывает persistent storage/shutdown failures
   независимо от screen; успешная загрузка истории не скрывает несохранённые данные.
   Settings пока не получает редактор channel secrets; устройство отображает
   public identity и существующие secret-free projections.

## Рекомендуемые подэтапы

Предлагаемые названия UI1–UI4 — обозначения в этом анализе, не выполненные этапы.
При принятии решения их можно добавить как C9.1–C9.4 перед C10, сохранив запись о
первоначально завершённом C9. Переписывать план так, будто новая навигация уже была
реализована, не следует.

### UI1 — выделение views с сохранением поведения

Цель: уменьшить обязанности MainWindow до shell/window lifecycle.
Scope: `ConnectionSettingsView`, `ChatWorkspaceView`/`ChatView`, перенос viewport и
scroll adapter к history control; typed bindings, явные activation/visibility и
focus contracts. Существующие VM и внешний вид максимально сохраняются.
Не входит: rail, новая палитра, отдельные device workflows, переделка Core.
Зависимости: C9 и короткий ручной baseline текущих scroll/search/draft/settings.
Тесты: скрытый/отсоединённый chat не меняет read watermark; повторный attach не
дублирует callbacks; selection/anchor/draft не сбрасываются; shutdown из Settings
ждёт прежних barriers; существующие Core/Desktop/library regressions.
Готово: MainWindow не обращается напрямую к внутреннему history list/search TextBox;
компоненты имеют понятное владение, baseline сохранён. Ручная UI-проверка обязательна.
Сложность: средняя; основной риск — scroll/read lifecycle.

### UI2 — rail и полноэкранная область выбранного раздела

Цель: реализовать описанную пользователем структуру окна.
Scope: shell sections Channels/Personal/Devices/ConnectionSettings, rail сверху/
снизу, mapping к workspace, возврат из Settings, постоянные status/node context,
адаптивный header и breakpoint по workspace width. Settings открывается в правой
области, не overlay. Исправить shortcuts для видимого экрана.
Не входит: новая телеметрия, отправка, пер-node кеш всех viewport, отдельная ОС-window.
Зависимости: UI1.
Тесты: выбор каждой section, Settings → назад сохраняет chat context; connect A/B
на Settings выбирает фактическую node history; Offline selector доступен только
Offline; incoming в скрытом чате не считается прочитанным; narrow Back/focus/IME;
переходы без второго connection loop, subscriptions и без send/advert/mutation.
Готово: каждая иконка показывает свой screen, status/error доступны всегда; добавление
нового раздела не требует копировать shell. Ручная wide/narrow проверка обязательна.
Сложность: средняя.

### UI3 — отдельный список и read-only карточка устройств

Цель: Devices выглядит как устройство, а не обычный чат с отключённым вводом.
Scope: service directory, DeviceDetailsView на существующих metadata, type-specific
labels и nullable fields; read-only служебная история остаётся доступной. Сохранить
unknown и отсутствующие в актуальном справочнике записи; не создавать Conversation
при простом открытии карточки. При необходимости выделить Devices VM, сохранив
одного владельца активной history и node context.
Не входит: remote CLI/status/telemetry/login, карта, mutations и live online badge.
Зависимости: UI1–UI2.
Тесты: Repeater/Room/Sensor/None/unknown и отсутствующий metadata; full key/node
ownership, nullable path/coordinates, late details A после B; ни editor/send, ни
network commands от открытия; read-only сообщения и unread доступны корректно.
Готово: устройство отделено от Chat client, metadata честно подписаны, исходящая
команда не вызывается. Ручная проверка с fake fixtures обязательна.
Сложность: низкая–средняя при существующем read-only scope.

### UI4 — визуальное оформление и итоговая keyboard/theme проверка

Цель: согласованный компактный интерфейс с привычной структурой мессенджера.
Scope: semantic theme resources, rail icons, selected/focus states, row density,
chat bubbles/time/labels, header/search/draft spacing, empty/error states, длинный
Unicode/ключи, контраст system/light/dark. Применять shared styles внутри текущего
Avalonia проекта, без дополнительной UI-библиотеки.
Не входит: полный Telegram clone, анимационная система, localization framework,
tray/notifications, send/delivery UI до D.
Зависимости: UI2–UI3.
Тесты: theme/geometry/shortcut regressions, layout на минимальном и wide размере,
ограниченный history viewport; UI snapshots/headless tests — если оправдано
инфраструктурой, не добавлять framework только ради сравнения каждого цвета.
Готово: согласован ручной wide/narrow вид, light/dark, keyboard/focus/IME; для
недоступной Windows платформенная проверка явно остаётся в E.
Ручная UI-проверка обязательна; автоматы не доказывают эстетическое качество.
Сложность: средняя, трудоёмкость зависит от числа итераций дизайна.

После UI4 выполняется существующий C10: интеграция A/B/A, scroll/read/search/draft,
writer failure/retry shutdown/restart, 100 000 сообщений и полный Release/regression.
Stage D сохраняет исходный scope отправки и управления контактами/каналами.

## Оценка объёма и затрагиваемые файлы

Для планирования: четыре отдельные агентские сессии реализации плюс ручные проверки
между ними и существующий C10. Это ориентир granularity, не обещание календарного
срока: UI1/UI2 могут потребовать дополнительной итерации из-за viewport/focus.
Визуально переложить всё можно быстрее, но подтверждение сохранности текущих
сценариев составляет значительную часть объёма.

Ожидаемые изменения:

- `Desktop/Views/MainWindow.axaml(.cs)` — shell вместо деталей всех screens;
  новые views в `Views/Chat`, `Views/Settings`, `Views/Devices`, `Views/Shell`.
- `MainWindowViewModel`, `ConversationNavigationViewModel` — section coordination
  и явный visible context; History/Draft VM — лишь необходимые lifecycle hooks.
- `Views/DesktopShortcutRouter.cs` — routing по активному workspace.
- `App.axaml` и новые shared styles/resources — оформление и vector icons.
- `Bootstrap/AppBootstrap.cs` — регистрация новых VM только при необходимости;
  компоненты не создают новый supervisor/storage.
- `Desktop.Tests` — visibility/navigation/ownership/shutdown regressions;
  `docs/testing` — ручная приёмка, после согласования — актуализация плана/архитектуры.

Core и MeshCoreSharp по текущим требованиям менять не нужно. Отдельные сборки для
компонентов, новая SQLite schema, Prism/ReactiveUI/navigation framework не нужны.
Удалённая информация о ретрансляторе является отдельной функциональностью вне
первого редизайна, если под «информацией» подразумеваются новые Mesh-запросы.

## Проверка исходной точки

Перед коммитом C9 выполнены Release build (0 warnings/errors), Core 127/127,
Desktop 111/111, MeshCoreSharp 92/92 и `git diff --check`.
C9 зафиксирован отдельным коммитом `dfb9648`.
Служебные изменения `.DS_Store` не включались в коммит и не удалялись.
Production/test-код при подготовке этого анализа не изменялся.
