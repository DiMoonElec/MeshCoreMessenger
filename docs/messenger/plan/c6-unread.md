# C6 — непрочитанное и переход к первому непрочитанному

[Оглавление](../../MESSENGER_PLAN.md) · [Маршрутизация чтения](../../README.md)

#### C6 — непрочитанное и переход к первому непрочитанному

**Выполнено 30.09.2026.**

**Цель:** сохранять честный read position независимо для каждого диалога/ноды.

**Scope:** Core read count/first unread и монотонная writer-операция LastReadSequence;
считать только подходящие incoming записи, обновлять UI после commit. Метка
прочитанного требует активного окна, открытого диалога и видимого диапазона C5.
Для одного watermark принять консервативное правило: продвигать только просмотренную
непрерывную границу непрочитанных; просмотр более позднего участка сам по себе не
погашает пропущенный непрочитанный промежуток. Переход к первому непрочитанному через
position API C4; read writes входят в shutdown lifecycle.

**Не входит:** радиоквитанции прочтения, системные уведомления, per-message read flags,
автоматическое «прочитать всё» при выборе диалога. **Зависимости:** C4–C5.

**Обязательные тесты:** неактивное/свёрнутое окно, другая вкладка/нода, только часть
страницы видима; непрочитанный gap и jump-to-latest; incoming во время read commit;
повтор/обратный watermark, outgoing не увеличивает count, рестарт, writer failure
не выдаёт ложный успех, shutdown дожидается принятых read writes.

**Готово:** unread сохраняется после рестарта и уменьшается только по принятому
правилу просмотра. **Ручная UI-проверка:** обязательна — фокус окна, скролл,
первое непрочитанное и поступление новых сообщений во время чтения.

Фактически добавлен `IConversationReadStateStore` поверх уже существующего
`Conversations.LastReadSequence`; SQLite schema не менялась. Read projection
node-scoped и содержит монотонный watermark, число только incoming после него и
точную `HistoryMessagePosition` первого непрочитанного. Writer проверяет точное
соответствие node/conversation/message/sequence, не допускает обратного движения и
возвращает состояние только после commit. Directory projection считает unread в
том же node scope; список диалогов показывает badge, ограниченный визуально `99+`.

C5 viewport теперь отбрасывает виртуализированные cache-containers вне видимой
области. `HistoryWindowViewModel` не пишет read state при открытии, загрузке страницы
или jump-to-latest. Продвижение разрешено только активному и видимому окну, когда
первый непрочитанный входит в фактический visible range; gap выше viewport остаётся
непрочитанным. Кнопка unread загружает bounded страницу вокруг первой непрочитанной
позиции и сохраняет её anchor. Incoming во время read commit остаётся unread до
фактического появления в видимом диапазоне; late completion старого контекста
игнорируется.

Первая ручная проверка C6 выявила остановку продвижения при непрерывном быстром
scroll: visible ranges, пришедшие пока предыдущий SQLite write был в полёте,
отбрасывались, после чего уже подтверждённый first-unread оказывался выше viewport
и корректная gap-защита не позволяла продолжить. ViewModel теперь накапливает
перекрывающиеся или соседние фактически просмотренные диапазоны до завершения write
и coalesce-ит их в более позднюю цель. Диапазон с реальным пропуском по-прежнему не
продвигает watermark. Сценарий закреплён deterministic regression test; повторная
ручная проверка подтвердила исправленное продвижение unread при прокрутке.

В ходе той же ручной проверки уточнена node-navigation policy: после Identify UI
автоматически открывает историю фактически подключённой ноды и блокирует selector
до полного перехода в Offline. Поэтому подключение больше не требует действия
«К активной», а reconnect не может оставить открытой историю другой ноды. Старое
значение Settings `desktop.follow-active-node` остаётся безвредным legacy и больше
не читается; схема SQLite не менялась. Тесты покрывают offline B -> synchronizing A,
запрет смены на B при соединении и повторное разрешение выбора после Offline.

`ConversationReadStateTracker` сериализует и coalesce-ит принятые цели разных UI
сигналов. Ошибка не уменьшает отображаемый unread, сохраняет pending target и
останавливает успешный shutdown; повторное закрытие выполняет retry без повторного
quiesce UI/connection. UI stop дожидается уже начатых задач, а общий shutdown имеет
отдельный read-state durable barrier после ingress/session barriers.

Пять новых Core tests проверяют реальный SQLite incoming-only count/first unread,
точный node scope, outgoing, repeat/reverse watermark, рестарт/directory projection,
serialization/coalescing и failure/retry tracker. Одиннадцать новых Desktop tests
проверяют inactive window, видимый gap и частичный диапазон, отсутствие записи от
page load/jump-to-latest, переход к first unread, writer failure, accepted-write
shutdown barrier/retry, incoming во время commit, late completion другой ноды,
и unread badge; существующий bootstrap test дополнительно проверяет DI wiring.
Release build прошёл без предупреждений; Core tests —
119/119, Desktop — 86/86, MeshCoreSharp — 92/92; `git diff --check` чист. C7 ещё не
начинался. В повторной ручной сессии подтверждены offline history, отображение
unread badge, уменьшение счётчика только при прокрутке просмотренного диапазона и
доступность selector нод после перехода в Offline; приложенный скриншот зафиксировал
18 непрочитанных в непросмотренном канале. После этой проверки пользователь принял
C6 к фиксации.
