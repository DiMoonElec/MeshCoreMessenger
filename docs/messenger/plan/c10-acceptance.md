# C10 — совместная приёмка Stage C

[Оглавление](../../MESSENGER_PLAN.md) · [Маршрутизация чтения](../../README.md)

#### C10 — совместная приёмка Stage C

**03.10.2026: автоматическая приёмка выполнена; финальная ручная приёмка C10
ожидается. Stage D не начат.** [Отчёт и ручной сценарий](../../testing/stage-c-acceptance.md).

Добавлен Desktop-тест с настоящей SQLite и 100 000 сообщений: полный двунаправленный
scroll, bounded DTO, поиск/переход, фактическое чтение, offline restore → fake Online
A/B/A, изоляция late B commit, два draft owner, ошибка shutdown/retry и новый startup.
Переиспользована fixture UI6. Существующий Core end-to-end отдельно проверяет
supervisor/session/receive/ingest с fake Companion, единственную attempt и барьеры.

Обнаружена и исправлена race Desktop: refresh мог инвалидировать инициализацию node
context после выбора диалога, но до открытия history/draft. Refresh теперь ожидает
completion инициализации с lifetime cancellation; новая загрузка другой ноды не
ждёт старую. Queued-dispatcher regression фиксирует точный interleaving.
Core, schema, protocol и MainWindowViewModel не менялись.

Debug/Release build: 0 warnings/errors; Desktop 242/242 в обеих конфигурациях,
Release Core 131/131, MeshCoreSharp 92/92. Native Light/Dark 560/1040 на отдельной
large fixture: 500 DTO и 24 реализованных контейнера; transport не запускался.
Отложенные пользователем фильтры и группа unknown в приёмку не возвращаются.

**Цель:** доказать совместимость подэтапов и отсутствие регрессий Stage B.

**Scope:** интеграционный сценарий на временной SQLite: offline restore -> подключение
fake A/B/A -> выбранная история/вкладка -> scroll/read/search/draft -> commit failure
и retry shutdown -> рестарт. Fixture с 100 000 сообщениями и несколькими нодами,
измерение пиковых размеров DTO/коллекций и SQL/UI задержек в Release; отчёт с ОС,
архитектурой и результатами в docs/testing. Исправлять только обнаруженные нарушения
критериев C, новые возможности выделять отдельно.

**Не входит:** hardware send/advert/mutation, упаковка/подпись и release-приёмка E.
**Зависимости:** C1–C9.

**Обязательные тесты:** весь интеграционный сценарий выше, отсутствие смешивания нод
и двух reconnect loops, late read/commit после смены контекста, bounded memory при
длительном scroll, commit-before-UI и полный набор shutdown regressions. Все три
regression suites и Release build обязательны.

**Готово:** исходный checklist C подтверждён тестами и ручным отчётом; ограничения
платформ перечислены явно. **Ручная UI-проверка:** обязательна, по сценарию C10;
для A/B достаточно fake data/session, второй физической ноды не требуется.
