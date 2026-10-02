# C4 — API страниц истории и адресуемой позиции

[Оглавление](../../MESSENGER_PLAN.md) · [Маршрутизация чтения](../../README.md)

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
