05.10.2026: после P6 добавлена [таблица успешных доставок в карточке контакта](../../testing/contact-delivery-history-card.md): время ACK по часам ПК, все candidate routes/попытки, отдельный снимок readback, RTT, late/ambiguous labels, refresh и пагинация. UI читает существующую локальную аналитику; схема БД не меняется. Ручная приёмка ожидается. P7 не начат.

05.10.2026: завершён **P6** [автоматических повторов ЛС](d7-private-auto-retry.md): scoped ContactDeliveries read API с пагинацией, explicit configured/readback provenance и session-owned readback после нового ACK evidence. Late ACK получает отдельный снимок; readback failure не отменяет Delivered, enrichment SQL retry не выполняет radio replay. Аналитика переживает clear/удаление переписки и контакта. Schema v5 без миграции. Release build без warnings/errors; Library 105/105, Core 354/354, Desktop 318/318. Следующий P7 — incoming dedup/UI projections. [Отчёт P6](../../testing/private-delivery-history-reader.md).

05.10.2026: по решению пользователя [хэши пакетов и будущий debug RX](message-packet-hashes.md#решение-пользователя--отложить) отложены. Исследование сохранено как возможное будущее расширение; код/схема не менялись. Продолжаем P6 автоматических повторов ЛС.

05.10.2026: scope [хэшей пакетов](message-packet-hashes.md#уточнение-пользователя-сначала-outgoing-rx-позднее) сокращён: outgoing hashes сначала, debug RX/correlation позже. Из доступных данных локально вычислимы канальные TX; для ЛС требуется дополнительный crypto source/Companion extension. БД проектируется под множество hash events, позднюю привязку и наблюдаемые пути. Изменён только анализ, реализация не начата.

05.10.2026: подготовлено [исследование хэшей сообщений](message-packet-hashes.md): collection packet observations с отдельным временем ПК на каждое событие, связь с attempts/receptions и общий UI сведений. Выявлен gap штатного Companion для точной привязки хэшей ЛС; варианты решения изложены, реализация не начата.

05.10.2026: пользователь подтвердил работу попыток ЛС P5 и обмен с ближней нодой. Доставка на дальнюю ноду ещё не проверена полноценно; необходимы детальные ручные испытания, причина неудачи не установлена. [Отчёт](../../testing/private-fallback-retries.md).

05.10.2026: завершён **P5** [автоматических повторов ЛС](d7-private-auto-retry.md): 3 known TX → owned reset/readback/local commit → 2 flood TX с новым timestamp, оба RetryMode, один пузырёк и progress 1/5…5/5. Late ACK первой фазы останавливает цикл; ошибки сброса/БД и отмена не replay mutation. Проверены actual MSG_SENT mode, обновление known-пути и условный reset learned-пути в flood-фазе. Release build без warnings/errors; Library 105/105, Core 345/345, Desktop 318/318. Следующий P6 — provenance/чтение истории успешных маршрутов. [Отчёт P5](../../testing/private-fallback-retries.md).

05.10.2026: завершён **P4** [автоматических повторов ЛС](d7-private-auto-retry.md): production coordinator, bounded FIFO по контакту, durable Queued admission, child attempt leases исходной session и до трёх flood-передач в обоих RetryMode. Общий progress под пузырьком; late ACK останавливает job, disconnect/persistence recovery не replay. Known-route пока один TX D6; следующий P5 — 3 known + owned reset + 2 flood. Release build без warnings/errors; Library 105/105, Core 330/330, Desktop 318/318. [Отчёт P4](../../testing/private-delivery-coordinator.md).

05.10.2026: завершён **P3** [автоматических повторов ЛС](d7-private-auto-retry.md): общий ACK commit для push/waiter/Delivered transition, группировка по MessageId и защита cross-message collisions, атомарный итог цикла и история успешной доставки с candidate route snapshots. Late ACK не зависит от перевода часов ПК назад. Release build без warnings/errors; Library 105/105, Core 312/312, Desktop 315/315. Автоповторы ещё не включены; следующий P4 — session-owned coordinator. [Отчёт P3](../../testing/private-delivery-ack-commit.md).

05.10.2026: завершён **P2** [автоматических повторов ЛС](d7-private-auto-retry.md): SQLite v5, циклы/сетевые идентичности, атомарный PreparePrivateAttempt и устойчивый timestamp floor, снимки маршрута/времени ПК. После перезапуска цикл Unknown без replay. Таблицы аналитики созданы; запись подтверждённого успеха и общий ACK commit — следующий P3. Автоматические повторы в MessageService ещё не включены. [Отчёт P2](../../testing/private-delivery-storage.md).

# План реализации MeshCoreMessenger

05.10.2026: завершён **P1** [автоматических повторов ЛС](d7-private-auto-retry.md): библиотечный SendTextAsync с явными timestamp/attempt 0…3, Core adapter, immutable policy двух режимов и планировщик 3/3+2. Первая часть не включает automatic TX: MessageService остаётся одноразовым, schema v4; следующий шаг P2 (БД/prepare/reservation). Release build без warnings/errors; Library 105/105, Core 282/282, Desktop 315/315. [Отчёт P1](../../testing/private-retry-api.md).

05.10.2026: сохранено [исследование packet hash/attempt/ACK](../../library/protocol/private-retry-hashes.md) и подготовлен [итоговый план автоматических повторов ЛС/истории успешных маршрутов](d7-private-auto-retry.md). Уточнённая политика пользователя: 3 flood либо 3 known при T1/attempt 0…2 + reset + 2 flood при новом T2/attempt 0…1. API выбирает повтор с прежним timestamp/увеличением attempt либо новый timestamp/attempt 0; один локальный пузырёк на цикл. Attempt 4 не нужен, лимит 160 остаётся; возможный дубликат у адресата при смене timestamp указан явно. Реализация не начата, изменена только документация.

05.10.2026: пользователь подтвердил работу обоих канальных действий и провёл [аппаратный эксперимент дедупликации](../../testing/channel-repeat-hardware-dedup.md): повтор доставки сохранил packet hash и не создал дубликат у получателя; отправка как нового изменила hash и создала второе сообщение. Прямые и ретранслированные копии проверены по raw hex.

05.10.2026: канальные повторы теперь имеют [два действия](../../testing/channel-repeat-actions.md): «Повторить доставку» (прежний timestamp/пузырёк) и «Отправить как новое» (новый timestamp/сообщение). Оба сохраняют черновик. Временный эксперимент стал первым из этих действий; личный повтор D7 не реализован.

04.10.2026: по запросу пользователя был включён временный [эксперимент повторов с исходным timestamp](../../testing/channel-repeat-dedup-experiment.md). Рабочий D7.1 с новым timestamp сохранён в `2a0919f`; аппаратный результат эксперимента ожидается.

04.10.2026: реализован [D7.1 — ручной повтор каналов без вопросов](../../testing/channel-manual-repeat.md):
новая попытка того же сообщения, сохранённый текст и новый durable wire timestamp,
прежний пузырёк/черновик. Личный повтор D7 не реализован; аппаратная приёмка ожидается.

04.10.2026: упрощены заголовки переписок: Public — только название; Private —
название и `Маршрут: direct / широковещательный / n хопов`. Число хопов берётся
из route descriptor контакта; недоступные данные обозначаются `неизвестен`.
Пользователь подтвердил ручную приёмку заголовков. Следующий последовательный
подэтап Stage D — [D7: явный повтор](d7-manual-retry.md); канальная часть выполнена, личная ожидается.

04.10.2026: добавлена [карточка «О канале»](../../testing/modal-channel-card.md)
в меню публичного чата. Карточка контакта подтверждена пользователем и зафиксирована
в `31d3b67`. Карточка канала подтверждена пользователем и зафиксирована в `a9bbdc1`.

04.10.2026: добавлены [переиспользуемый modal host и карточка «О контакте»](../../testing/modal-contact-card.md)
в меню приватного чата. Автоматические проверки завершены; ручная приёмка ожидается.

04.10.2026: реализованы [актуализация маршрута по PATH_UPDATED и точечный readback
после сброса](../../testing/live-contact-routes.md). Переподключение для отображения
обученного маршрута не требуется. Пользователь подтвердил аппаратную приёмку 04.10.2026.

04.10.2026: добавлена [обработка позднего ACK ЛС](../../testing/private-late-ack.md).
Unconfirmed может перейти в Delivered после timeout; обновляется сохранённая
attempt, новая передача не выполняется. Пользователь подтвердил ручную приёмку
04.10.2026; временный тайм-аут 1 мс удалён, восстановлено значение из профиля.

[Оглавление](../../MESSENGER_PLAN.md) · [Маршрутизация чтения](../../README.md)


Актуализация: 03.10.2026. **Этапы A и B выполнены, включая B7.5: локальное
хранилище, offline startup, приём, reconnect и восстанавливаемое закрытие готовы.
Профиль описывает транспорт; identity определяется по полному ключу в каждой
session. Stage C начат: C1–C9 завершены автоматически; ручная UI-проверка C7–C9
ожидается. Перед C10 согласована компонентная переработка UI; реализованы
UI1 (NavigationShellView), UI2 (ChatsView), UI4 (ConnectionSettingsView)
и UI5 (ApplicationSettingsView — выбор темы). Пользователь подтвердил ручную
проверку UI5 02.10.2026.
Пользователь подтвердил внешний вид чата; ручная приёмка подключения ожидается.
После UI4 статус перенесён из глобальной панели в динамический заголовок окна;
панель и резервные верхние отступы убраны, суффикс папки данных сохранён.
Пользователь подтвердил корректность этой доработки ручной проверкой 02.10.2026.
UI4 и UI5 выполнены раньше UI3 по запросу пользователя. UI3 реализован
02.10.2026: независимый read-only список устройств и карточка, поиск и paging,
адаптивная компоновка; пользователь подтвердил успешную ручную проверку.
Результаты: [UI3 — устройства](ui3-devices.md). UI6 реализован и автоматически
проверен 03.10.2026; общая ручная cross-screen приёмка ожидается:
[UI6 — интеграция](ui6-integration.md). C10 автоматически проверен 03.10.2026,
финальная ручная приёмка ожидается: [C10](c10-acceptance.md),
[нагрузочный отчёт и ручной сценарий](../../testing/stage-c-acceptance.md).
Stage D начат; 03.10.2026 зафиксирован [порядок D1–D18](stage-d.md):
UI-first для каждой группы функций, безопасные preview без эфира/записей в настоящую
историю. [D1](d1-send-ui.md) реализован и автоматически проверен: подписи под
пузырьками, composer presentation, contextual repeat и изолированный preview;
Desktop 251/251 Debug/Release, native preview/viewport audits пройдены.
Пользователь подтвердил визуальную ручную приёмку D1 03.10.2026.
[D2](d2-text-validation.md) реализован и автоматически проверен: общий UTF-8 validator, application
text processor-заглушка, живой счётчик и read-only readiness. Все уровни пока no-op;
настройка настоящего приложения/алгоритм замен — будущая задача.
Пользователь подтвердил ручную проверку D2 03.10.2026.
[D3](d3-outgoing-storage.md) реализован: atomic prepare/operation ID, CAS Sending,
сохранение TransmissionText/ACK metadata, миграция v3 с backup, startup/restore
recovery без TX, отдельные post-commit notifications и latest-attempt projection.
Ручная приёмка D3 ожидается.
[D4](d4-session-commands.md) реализован и автоматически проверен: Online admission/
immutable leases, typed adapters, owned workflows/observers, outgoing status Retry/Flush,
проверки перед invocation и teardown/next-session/shutdown commit barriers.
Runtime Send/mutations/advert в рамках D4 не подключались.
[D5](d5-channel-send.md) реализован: production отправка в канал, byte processor capture,
revision-safe draft transfer, post-commit bubble/status, выбор неоднозначного слота,
Enter/Shift+Enter/IME и single-flight. OK означает AcceptedByNode, без channel ACK.
Debug/Release: библиотека 96/96, Core 198/198, Desktop 277/277;
настоящий TCP-клиент проверен на loopback Companion emulator (OK/ERROR), нативный
send audit Light/Dark 420/960 пройден. Аппаратная нода не использовалась; по указанию
пользователя аппаратную приёмку он выполняет самостоятельно.
После аппаратной проверки пользователь обнаружил блокировку send при переходе
между публичными чатами; исправлена публикация draft owner/CanEdit из фонового
потока. Native ChatsView/ListBox A → B → A проверен в Light/Dark без incoming,
добавлены dispatcher/startup регрессии. Пользователь повторил аппаратный сценарий
и подтвердил: баг исправлен.
[D6](d6-private-send.md) реализован: private Chat send, 160-byte processor capture,
MSG_SENT/tag commit до terminal status, session-owned ACK observers и UI wiring.
Две pending отправки сопоставляются по protocol expected_ack, в том числе в обратном
порядке; повторный/посторонний ACK не меняет чужую attempt. Debug/Release:
библиотека 96/96, Core 230/230, Desktop 281/281; восемь TCP-emulator сценариев.
После пользовательской проверки исправлен ResetSearch при фоновом OpenAsync:
search-command notifications теперь проходят через UI dispatcher, inline startup сохранён.
Desktop 284/284 Debug/Release; native first-private-send audit Light/Dark × 420/960
пройден, прежняя ошибка запуска RenderTimer больше не воспроизвелась. Пользователь
подтвердил 03.10.2026: открытие новой переписки ЛС больше не вызывает ошибку.
Автоматических/ручных повторов D6 нет.
D7 частично выполнен для каналов; личная часть, D8–D13 и D16–D18 не начаты.
D14/D15 реализованы досрочно по отдельному запросу пользователя: подтверждение
и транзакционная локальная очистка. Debug/Release: Core 243/243, Desktop 291/291.
Native audit заблокирован RenderTimer -6661; ручная приёмка ожидается.
[Отчёт очистки](../../testing/history-clear.md).

Перед продолжением Stage D пользователь запросил управление маршрутом ЛС:
сначала изменения существующего UI, затем новые элементы, затем сброс маршрута.
Первое изменение реализовано 03.10.2026: общая кнопка перехода к последнему
сообщению перенесена из заголовка в нижний правый угол над composer; круглая
кнопка со стрелкой вниз занимает отдельное место и не перекрывает пузырьки.
Команда и правила доступности сохранены для Public и Private.
Desktop Release 284/284; native Public/Private send audits Light/Dark × 420/960
пройдены без аппаратной ноды. Пользователь принял перенос кнопки и запросил commit 03.10.2026.
Следующее UI-изменение реализовано 03.10.2026: справа в заголовке Public/Private
добавлено меню переписки (MenuFlyout) с шестью тестовыми пунктами по референсу.
Иконка `Assets/Icons/chat_menu.png` используется как alpha mask с динамическим
SystemControlForegroundBaseHighBrush, аналогично боковой навигации.
Пункты помечены «тест» и не связаны с командами; вызов сброса маршрута ещё не реализован.
Debug build: 0 warnings/errors; native открытие меню Light/Dark × 420/960:
шесть пунктов, меню помещается по ширине; публичная отправка с эмуляцией проходит.
Desktop Release: 284/284 теста пройдены.
Пользователь принял меню и запросил commit 03.10.2026.
Пункт удаления истории теперь функциональный; остальные пять остаются тестовыми.

D4 Debug/Release: библиотека 96/96, Core 180/180, Desktop 267/267.
Ручная аппаратная проверка D4 не требуется.
D3 Debug/Release: библиотека 96/96, Core 159/159, Desktop 265/265.
Первый Release Desktop runner завис; диагностический повтор прошёл, детали в D3.
 Для будущей доработки зафиксировано обрезание лишних символов
в composer при превышении лимита; сейчас оно не реализовано, детали — в D2.
D2 Debug/Release: библиотека 96/96, Core 139/139, Desktop 256/256.
Фильтры каналов и отдельная
визуальная группа unknown сознательно отложены: семантику/алгоритм нужно переосмыслить
будущей задачей, а не возвращать в рамках UI6/C10.
После ручной проверки UI6 исправлен сброс Public → Private → Public:
[два сохраняемых chat workspace](chat-workspaces.md), независимые selection/search/
history/draft/viewport при общих services. Пользователь подтвердил ручную проверку
исправления 03.10.2026: явных проблем не заметил.
В C10 исправлена race refresh/node initialization, добавлена SQLite-приёмка
100 000 сообщений; Desktop 242/242 Debug/Release, Core 131/131 и библиотека 92/92
Release. Выбор Serial-порта и создание второго профиля подтверждены пользователем;
понятность создания профиля оставлена отдельным UX-улучшением.
После ручного C10 обнаружен скачок viewport при подгрузке; исправлен с измерением
до/после и отдельными scroll intents. Native Light/Dark: три сценария дают
0 DIP / 0 промежуточных read advances; пользователь подтвердил ручную проверку
03.10.2026: подгрузка вперёд/назад без рывков.
[Отчёт viewport](../../testing/history-viewport-preservation.md), Desktop 243/243.
Прокрутка сверхдлинных fixture-сообщений сознательно отложена 03.10.2026:
[известное ограничение](../../testing/known-ui-limitations.md). Вернуться при
воспроизведении на реальных сообщениях либо поддержке больших сообщений;
в новых fixture исходный набор вынесен в отдельный стресс-диалог.
После UI3 реализованы [rounded mention chips](chat-mention-chips.md) в теле сообщений
и ПКМ → «Копировать» из исходной модели, вместо первоначального Run-варианта;
автоматические проверки завершены, пользователь подтвердил успешную ручную проверку.
Offline UI теперь показывает только историю
последней подключённой ноды, без выбора другой сохранённой ноды.**
Библиотечный фундамент уже реализован: USB/Serial, TCP, сообщения/ACK,
контакты, каналы, адверты, информация, статистика и барьер callbacks;
базовая проверка — 92 теста.
Архитектурные решения и инварианты: [MESSENGER_ARCHITECTURE.md](../../MESSENGER_ARCHITECTURE.md).
Этот план не заменяет [план библиотеки](../../../PLAN.md).


Уточнение 04.10.2026: очистка истории подтверждена пользователем, native audit
Public/Private × Light/Dark × 420/960 прошёл после успешного display preflight.
[Особенность macOS startup](../../testing/viewport-audit-startup.md) документирована;
добавлены read-only проверки в диагностической утилите.
[Анализ ручного сброса маршрута ЛС и разных меню](private-route-reset.md): предложение,
функциональность сброса пока не реализована.

Первый этап подготовки сброса маршрута выполнен 04.10.2026: состав и команды меню
создаются при инициализации каждого chat workspace; Public не содержит маршрутных
пунктов, Private содержит заглушки. Очистка переведена на command binding с прежним
подтверждением. [Реализация и проверки](../../testing/conversation-menu-initialization.md):
Desktop 294/294 и native 8/8. Сам механизм ResetPath не реализован.

Второй этап ручного сброса маршрута ЛС реализован 04.10.2026: приватный пункт меню
→ owned Core workflow → ResetPathAsync → полный contact readback → точечная запись
route descriptor (SQLite v4). Send/ACK и reset координируются общим guard; история
и drafts сохраняются. [Проверки и ограничения](../../testing/private-route-reset.md):
Library 97/97, Core 253/253, Desktop 305/305; native reset 4/4, clear 8/8.
Пользователь подтвердил работоспособность сброса маршрута 04.10.2026.
