# C2 — read projections диалогов и справочников

[Оглавление](../../MESSENGER_PLAN.md) · [Маршрутизация чтения](../../README.md)

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
