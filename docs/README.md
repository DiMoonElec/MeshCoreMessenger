# Документация: выбор разделов для задачи

Основные документы — оглавления. Читать все ссылки подряд не требуется: выбрать
нужный маршрут ниже, затем исходники и тесты затрагиваемого компонента. Подробности
соседних этапов открывать только при зависимости или противоречии. Законченные
checkpoints описывают результаты на свою дату, а не всегда текущую архитектуру.

## Точки входа

- [Старт агентской сессии](CODEX_START.md) — порядок чтения и готовый prompt.
- [Текущее состояние Messenger](messenger/plan/current-state.md) — статус Stage C.
- [Актуальный UI1–UI6](messenger/plan/ui-components.md) — согласованный порядок
  компонентной переработки и результаты панели; следующая реализация — по запросу пользователя.
- [Архитектура Messenger](MESSENGER_ARCHITECTURE.md) — оглавление application contracts.
- [План Messenger](MESSENGER_PLAN.md) — отдельные этапы, история результатов и тесты.
- [Архитектура библиотеки](ARCHITECTURE.md) — transport/runtime/transactions.
- [Companion protocol](COMPANION_PROTOCOL.md) — кадры, команды, ответы и push.
- [Roadmap библиотеки](ROADMAP.md) — milestones и границы будущих функций.
- [Анализ UI](UI_COMPONENTS_AND_REDESIGN_ANALYSIS.md) — обоснование компонентов;
  первоначальное четырёхшаговое предложение историческое, актуальный план — UI1–UI6.
- [Аппаратные и ручные отчёты](testing/) — читать только отчёт нужного сценария.
- [Известные ограничения UI](testing/known-ui-limitations.md) — отложенная прокрутка
  сверхдлинных сообщений, подтверждённые факты и условия возврата к задаче.

## Минимум перед application-работой

Прочитать `AGENTS.md` в корне, текущее состояние и следующие короткие контракты:

- [Правила агентов](messenger/architecture/agent-rules.md) — сохраняемые инварианты.
- [Владение состоянием](messenger/architecture/ownership.md) — роли application services.
- [Identity](messenger/architecture/identity.md) — полный ключ ноды, контакта и канала.

Затем выбрать тематический маршрут; история Stage A/B целиком не нужна для UI.

## Маршруты

| Задача | Дополнительные разделы |
| --- | --- |
| Shell, стили, новые UI-компоненты | [UI-план](messenger/plan/ui-components.md), [node-scoped UI](messenger/architecture/node-scoped-ui.md), [владельцы компонентов](messenger/ui-analysis/components.md), [риски UI](messenger/ui-analysis/risks.md) |
| История, scroll, unread | [C4 position API](messenger/plan/c4-history-api.md), [C5 viewport](messenger/plan/c5-history-window.md), [C6 unread](messenger/plan/c6-unread.md); выбрать затрагиваемую часть |
| Search/draft/preferences | [C7 search](messenger/plan/c7-search.md), [C8 draft](messenger/plan/c8-drafts.md), [C9 preferences](messenger/plan/c9-preferences.md), [durable UI-записи](messenger/architecture/ui-and-local-writes.md); выбрать нужный этап |
| Connect/reconnect/session | [Connections](messenger/architecture/connections.md), [B6 supervisor](messenger/plan/b6-supervisor.md), [финальная identity B7.5](messenger/plan/b7-5-identity.md) |
| Persistence/ingest/schema | [Storage](messenger/architecture/storage.md), [B3 directory](messenger/plan/b3-directory.md), [B4 ingest](messenger/plan/b4-ingest.md), [B5 receive](messenger/plan/b5-receive.md); выбрать соответствующий owner |
| Shutdown/lifecycle | [Shutdown contract](messenger/architecture/shutdown.md), [B7.3](messenger/plan/b7-3-shutdown.md), [event barriers/message pump](library/architecture/message-pump.md), C6/C8/C9 writer barriers при их изменении |
| Пути данных / несколько экземпляров | [Папки данных и запуск](messenger/architecture/data-directories.md), начало [Storage](messenger/architecture/storage.md), [A4.1 instance lock](messenger/plan/stage-a.md#a41--один-экземпляр-на-каталог-данных-выполнено-25092026), [Shutdown contract](messenger/architecture/shutdown.md) |
| Library framing | [Layers](library/architecture/layers.md), [runtime](library/architecture/runtime-pipeline.md), [frame formats](library/protocol/framing.md), [приоритет источников](library/protocol/sources.md) |
| Routing/concurrency/timeouts | [Immediate transactions](library/architecture/immediate-transactions.md), [matching](library/architecture/packet-matching.md), [gates](library/architecture/concurrency.md), [timeouts/errors](library/architecture/api-and-errors.md), соответствующая protocol-команда |
| Приём/отправка сообщений | [Message pump](library/architecture/message-pump.md), [incoming wire](library/protocol/incoming-messages.md), [send/ACK wire](library/protocol/outgoing-messages.md); application send scope — [Stage D](messenger/plan/stage-d.md) |
| Реализация Stage D | [Порядок D1–D18](messenger/plan/stage-d.md), [исходное состояние/контракты](messenger/plan/stage-d-start.md), затем только файл выбранного подэтапа и его зависимости; старт — [D1 UI отправки](messenger/plan/d1-send-ui.md) |
| Приёмка | [C10](messenger/plan/c10-acceptance.md), [матрица](messenger/plan/test-matrix.md), [Stage E](messenger/plan/stage-e.md), нужный отчёт testing |

## Изменение и проверка документов

Фактические результаты записывать в файл соответствующего этапа. Оглавление
менять при добавлении/перемещении раздела; текущий статус — в `current-state.md`.
Не копировать все checkpoints в каждый новый файл и не переписывать исторические
решения как изначально принятые. B7.5 заменяет раннюю привязку profile → expected key.
Проверять относительные ссылки после перемещения. Общие индексы сохраняют прежние
пути, поэтому внешние ссылки README/PLAN/AGENTS продолжают вести к документации.
