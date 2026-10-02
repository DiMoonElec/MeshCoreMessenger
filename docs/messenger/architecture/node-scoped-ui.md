# 8. UX первой версии

[Оглавление](../../MESSENGER_ARCHITECTURE.md) · [Маршрутизация чтения](../../README.md)

## 8. UX первой версии

### Контекст ноды и чтения (C1–C3 реализованы)

`ActiveNodeId` определяется только текущей session и snapshot supervisor после
Identify. Данные для подписи берутся из Nodes по этому ID; имя/endpoint не служат
ключом. До Identify active node неизвестна; во время Synchronizing она уже может
быть известна, но это ещё не Online. Профиль активной attempt берётся из
Snapshot.ProfileId, а выбор в редакторе профиля отображается отдельно.

Глобальная панель статуса удалена после UI4. Заголовок окна показывает
`MeshCore Messenger - Отключено` либо текущую фазу supervisor; только Online
добавляет `[имя ноды (первые 12 hex-символов ключа…)]` по ActiveNode, не ViewedNode.
Для нестандартной папки остаётся прежний суффикс ` — имя папки`. Подробные сведения,
полный ключ, ошибки и connect/disconnect доступны на вкладке «Подключение».

`ViewedNodeId` — собственная нода, чья история открыта в UI. Уточнение UI2
(02.10.2026) заменяет прежний offline selector: теперь в любом состоянии ручного
выбора другой ноды нет. Offline startup восстанавливает
`desktop.last-connected-node-id` с проверкой существования; при отсутствии ключа
выбирает наиболее недавно LastSeenUtc ноду (legacy/fixture), при отсутствии нод —
пустое состояние. Прежний ручной `desktop.viewed-node-id` игнорируется.
После Identify, начиная с `Synchronizing`, UI выбирает и сохраняет фактическую
active node. Reconnect заново определяет identity. Disconnect очищает active identity,
но сохраняет последнюю историю. Connected UI не показывает историю B при session A.

Навигация, запросы страниц/поиска и записи read position/draft несут NodeId. Core
проверяет принадлежность ConversationId ноде. Контекст UI имеет собственную revision
(node/tab/conversation/query), независимую от connection generation: результаты
устаревших чтений не применяются, а старые committed сообщения остаются в своей БД-
истории. Источник обновлений UI — post-commit уведомления и чтение SQLite; UI не
становится consumer `CompanionSession.Events`. Завершение directory sync обновляет
read projections даже без новых сообщений. Подробный порядок реализации — C1–C10
в MESSENGER_PLAN.md.

Минимальный C1 shell уже следует этой модели: bounded список Nodes читается через
`INodeStore`, история — через node-scoped `ILocalHistoryReader`, а post-commit DTO
содержит сохранённый NodeId. Последний `ViewedNodeId` хранится в Settings; прежняя
настройка режима следования оставлена только как игнорируемый legacy-ключ. Сам по
себе C1 не означал готовность directory projections, вкладок,
постраничной навигации, unread или drafts.

C2 добавил read-only `IConversationDirectoryReader`: отдельные bounded страницы
Chat/служебных контактов, каналов и двух unknown-групп, а также secret-free карточки.
Stable key строится из сохранённой полной identity, не из имени или ConversationId;
cursor дополнительно привязан к NodeId и section. Directory-only entry имеет nullable
ConversationId и не создаёт пустую историю. Один channel fingerprint агрегирует
несколько активных slots; AccessKind только читается из БД и не выводится из имени.
В C3 этот API подключён к отдельной `ConversationNavigationViewModel`: XAML показывает
«Личные», «Каналы» и «Устройства», отдельные unknown-группы и фильтр каналов по
сохранённому `AccessKind`. Выбор вкладки и stable key последней записи сохраняются
отдельно для каждой просматриваемой ноды. Directory-only entry открывает пустую
read-only карточку без создания Conversation; Online после directory sync и
post-commit уведомление перечитывают committed projection. Результаты чтений имеют
собственную context revision, поэтому поздняя загрузка старой ноды не меняет новую.
`MainWindowViewModel` остаётся владельцем connection/active/viewed-node shell, а
navigation ViewModel владеет списками, текущей короткой историей и narrow-layout
переходом «Назад». Полноценные страницы/viewport длинной истории остаются C4–C5.

C4 добавляет к `ILocalHistoryReader` единый позиционный контракт длинной истории.
`HistoryMessagePosition` всегда содержит NodeId, ConversationId, устойчивый MessageId
и глобальный LocalSequence; reader проверяет как node ownership диалога, так и точное
соответствие MessageId/LocalSequence. Страницы before/after и окно around позиции
возвращаются хронологически, ограничены 500 DTO и сообщают `HasEarlier`/`HasLater`.
Порядок и границы строятся только по LocalSequence, поэтому ошибочный или одинаковый
wire timestamp не влияет на навигацию. DTO дополнительно сохраняет nullable TextType,
BinaryDataType, WireTimestamp и ResolutionState, но не экспортирует binary payload.
Существующий индекс `IX_Messages_Conversation_Sequence` покрывает range-запросы;
новая миграция для C4 не потребовалась. UI viewport начинает использовать контракт
только в C5.

C5 выделяет `HistoryWindowViewModel` как владельца активного окна истории. Оно
загружает страницы по 100 сообщений через C4 и удерживает не более пяти страниц /
500 `HistoryMessageListItem`: prepend удаляет дальний конец, append — дальнее начало,
а выгруженный диапазон остаётся повторно доступен по position API. ObservableCollection
меняется только через UI dispatcher (кроме синхронной pre-loop загрузки startup).
Directory refresh сохраняет окно при том же stable conversation, поэтому commit
больше не вызывает полный Clear/reload.

Viewport сообщает фактически реализованный диапазон LocalSequence, активность окна
и нахождение у визуального конца, но C5 ничего не записывает как прочитанное. При
prepend UI получает sequence прежнего первого элемента как stable anchor; при
commit автопрокрутка разрешена только у фактического конца. В середине истории
commit лишь создаёт дедуплицированный индикатор новых сообщений и доступную newer-
страницу; команда к последнему сообщению перечитывает bounded latest page. Список
использует `VirtualizingStackPanel`, а тело сообщения — `SelectableTextBlock` для
Unicode copy. Binary, CLI/signed text и room-post получают честные read-only labels.
Эти visible-range сигналы становятся входом C6, но read watermark в C5 отсутствует.
