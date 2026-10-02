# C7 — локальный поиск и переход к результату

[Оглавление](../../MESSENGER_PLAN.md) · [Маршрутизация чтения](../../README.md)

#### C7 — локальный поиск и переход к результату

**Цель:** находить диалог и сообщение без выгрузки всей истории в UI.

**Scope:** bounded поиск имён внутри выбранной ноды/вкладки; поиск текста внутри
выбранного диалога, постраничные результаты с message position, переход/подсветка
через C4–C5. Литеральный Unicode substring; явно зафиксировать регистрозависимое
сравнение первой версии, не обещать Unicode case folding от SQLite NOCASE.
Debounce на injectable time, отмена и проверка query revision. Запрос выполняется
вне UI; cancellation длительного SQL требует отдельной проверки, поскольку нынешний
DatabaseReader проверяет token только перед action, а LIMIT не ограничивает scan.

**Не входит:** FTS, поиск сразу по всем нодам, regex, semantic search, экспорт.
**Зависимости:** C2–C5; после C6, чтобы поиск не обходил правила read position.

**Обязательные тесты:** кириллица/emoji, literal `%`/`_`/кавычки, пустой запрос,
границы страниц, isolation, быстрые последовательные запросы и stale results;
результат вне загруженного окна, deleted/missing target, поиск на 100 000 записей,
отсутствие UI blocking и ложного mark-read по одному поисковому результату.

**Готово:** поиск bounded по результатам, отзывчив и приводит к правильному
сообщению в своей ноде. **Ручная UI-проверка:** да, быстрый ввод, отмена, фокус,
переход назад в историю и Cmd/Ctrl для поиска.

Фактически реализовано 01.10.2026. `ConnectionProfile`/active-node модель не
изменялась. В Core добавлены node/conversation-scoped API поиска directory и
текстовых сообщений: страницы ограничены 200 и 100 результатами соответственно,
desktop запрашивает по 100 имён и 20 сообщений. Сравнение — параметризованный
регистрозависимый literal Unicode `instr`; wildcard-семантики для `%`/`_` нет.
Каждый message result несёт точный `HistoryMessagePosition`; переход загружает
bounded окно C4 вокруг target, прокручивает к нему и подсвечивает, но не вызывает
read-state write. Missing target и read failures отображаются немодально.

Оба UI-поиска используют инъецируемую задержку 250 ms, cancellation и query
revision; late result старого запроса не применяется. `DatabaseReader` регистрирует
`sqlite3_interrupt` на token, поэтому отменяется и уже исполняющийся SQLite scan.
В окне добавлены поля поиска имён и текущей переписки, paged result list,
«Показать ещё» и Cmd/Ctrl+F с выбором поля по текущему layout/selection. FTS,
глобальный all-node поиск, regex, semantic search и schema changes не добавлялись.

Детерминированные тесты покрывают кириллицу/emoji, регистр, literal `%`, `_` и
кавычки, пустой запрос, pagination, node/conversation isolation, 100 000 сообщений,
прерывание активного SQLite query, injectable debounce, rapid query/stale result,
переход к сообщению вне текущего окна, deleted target и отсутствие ложного
mark-read. Release build прошёл без предупреждений; Core tests — 122/122, Desktop —
91/91, MeshCoreSharp — 92/92; `git diff --check` чист. Ручная UI-проверка C7 пока
не выполнена; до неё проверить быстрый ввод/отмену, Cmd/Ctrl+F, выбор результата,
подсветку и возврат к истории. C8 не начат.
