# C8 — локальные черновики и durable UI-записи

[Оглавление](../../MESSENGER_PLAN.md) · [Маршрутизация чтения](../../README.md)

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
