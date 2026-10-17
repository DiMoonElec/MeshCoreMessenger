# REGION: поведение ноды и протокол регионов

Дата анализа: 16.10.2026. Статус: исследование исходников, не аппаратная приёмка.
[План внедрения REGION](../../messenger/plan/stage-region.md).

## Источники и граница достоверности

Проверены локальный код проекта и публичные исходники:

- MeshCore, commit `81dbae592ffad40b4212bc0b37e82b38da08f4bc`, на момент проверки
  HEAD и peeled tag `companion-v1.18.0` / `repeater-v1.18.0`.
- meshcore_py, commit `6bf0428ab10acb34bc6c1d0fdef4b5bdf17dc84c` — дополнительный
  источник форматов и примеров, не нормативный алгоритм приложения.
- Upstream `docs/companion_protocol.md` в том же firmware commit: описание неполное,
  поэтому одного перечня документированных команд недостаточно.

Аппаратный отчёт проекта содержит `v1.18.0-07a833bb`, protocol 14. Это другой
идентификатор сборки: совпадение версии не доказывает идентичность исходников.
Перед реализацией wire fixtures сверить с целевой сборкой; перед закрытием REGION
проверить поведение на ней. В этом исследовании подключений к устройству не было.
Загруженные reference checkout находятся в игнорируемом `tmp/`; воспроизводимые
ссылки ниже закреплены на commit и не зависят от этих каталогов.

## Что означает регион

Region / flood scope ограничивает распространение flood-пакетов через репитеры.
Это не радиочастотный preset, не геозона GPS, не канал, не список участников
и не дополнительный ключ шифрования содержимого канала.

Публичное имя задаёт 16-байтовый transport key: первые 16 байт
`SHA256(UTF8("#" + имя_без_первого_#))`. `foo` и `#foo` означают один публичный
регион; регистр и остальные байты значимы. Нельзя автоматически lower-case,
транслитерировать или Unicode-normalize имя. `*` означает unscoped, а не имя,
из которого надо вычислить ключ. Регион `$...` обозначает частный регион
репитера; его ключ не выводится из имени обычным публичным алгоритмом.

В эфир передаются два 16-битных transport code, то есть 4 дополнительных байта.
Первый вычисляется через HMAC по типу и payload конкретного пакета; это **не
постоянный хеш имени региона**. Второй в рассмотренном Companion устанавливается
в 0 (будущий return/home scope). Проверка кода требует ключа и самого пакета,
а совпадение короткого кода не даёт криптографически надёжной идентичности региона.
См. [TransportKeyStore][keys] и [RegionMap][map].

## Где что хранится

| Данные | Владелец / срок жизни | Что может прочитать приложение |
| --- | --- | --- |
| Каталог имён для выбора пользователем | Приложение; в Companion такой таблицы нет | Собственная БД, результаты запросов к репитерам |
| Регион конкретного канала | Приложение | Собственная настройка; GET_CHANNEL региона не возвращает |
| Default scope Companion: имя и ключ | Нода, постоянные preferences | GET_DEFAULT_FLOOD_SCOPE |
| Текущий override и force-unscoped | Companion RAM, глобальны для отправок | GET для override отсутствует |
| Иерархия регионов, allow/deny, home/default репитера | Репитер, RegionMap и постоянное хранилище | Discovery даёт только ограниченный список разрешённых имён; admin CLI — больше |

В [Companion MyMesh][companion] `send_scope` и `send_unscoped` инициализируются
при создании объекта. Обычный disconnect приложения не является сбросом ноды.
Override не одноразовый: после SEND он сам не исчезает. Перезапуск ноды сбрасывает
RAM, но восстанавливает постоянный default. Сборка может задавать начальный
`DEFAULT_FLOOD_SCOPE_NAME`.

Постоянный default записывается в `/prefs.json` (`defs_nm`, `defs_key`);
есть чтение старого формата preferences. В `/channels2` сохраняются reserved bytes,
имя и секрет канала, без региона. GET_CHANNEL/SET_CHANNEL также не имеют поля
scope. Основание: [DataStore][store], [NodePrefs][prefs], [Companion MyMesh][companion].

Следствие: другое приложение увидит default ноды, но не узнает наши назначения
регионов каналам. Синхронизацию этих назначений через Companion обещать нельзя.
Внутреннее хранилище закрытого мобильного приложения этим исследованием не проверялось.

## Выбор scope и области его действия

Для отправляемого через `sendFloodScoped` канального или личного пакета:

1. `send_unscoped == true` → без transport codes, даже при заданном default.
2. Ненулевой override key → этот ключ.
3. Иначе → постоянный default key.
4. Нулевой default key → без scope.

У per-channel overload в firmware прямо остаётся TODO про собственный scope
канала. Поэтому приложение перед каждой отправкой выбирает нужное состояние
глобального override. «Наследовать default» и «принудительно без региона» различны.
Direct-передача идёт по маршруту; нельзя обещать, что scope ограничивает direct.

Flood self-advert — отдельный случай: использует **постоянный default напрямую**,
игнорируя временный override. Zero-hop advert и discovery control отправляются
zero-hop. Обобщать действие команды 54 на абсолютно все виды передач нельзя.
Автономные ответы прошивки тоже нужно отличать от команд приложения.
Основание: `sendFloodScoped`, `CMD_SEND_SELF_ADVERT`, `CMD_SEND_CONTROL_DATA`
в [Companion MyMesh][companion], `sendAnonReq` в [BaseChatMesh][chat].

Companion не фильтрует обычные входящие сообщения по выбранному scope.
Выбор региона определяет исходящую flood-передачу, а не подписку на регион.
Форматы сохранённых incoming channel/private messages не содержат достоверного
имени/ключа региона: назначать входящему текущий выбранный регион неправильно.
Raw packet monitor может дать диагностические предположения, но не заменить
метаданные истории. Регион не должен менять идентичность канала или разрезать чат.

## Уточнение: ЛС, ACK и асимметричные маршруты

Повторная проверка 16.10.2026 по вопросу пользователя: применение scope к ЛС
подтверждено исходниками и [объяснением автора firmware][default-blog].
Default scope появился в firmware v1.15.0, настройка приложения описана для v1.43.0.
Для каналов есть отдельный override, для flood-ЛС штатная модель использует default
ноды. Отсутствие выбора региона у отдельного контакта этому не противоречит.
Предлагаемый explicit scope в библиотечном SendText — управление глобальным
wire override в операции, а не существующее поле региона контакта.

Путь plain-text ЛС в проверенном commit:

1. CMD_SEND_TXT_MSG вызывает BaseChatMesh::sendMessage.
2. При неизвестном out_path вызывается Companion::sendFloodScoped(ContactInfo),
   который выбирает force-unscoped / override / default. Иначе вызывается sendDirect.
3. Получатель передаёт сообщение в onMessageRecv **до** отправки ACK.
4. На flood-ЛС получатель создаёт PATH с вложенным ACK и отправляет его flood
   через собственный sendFloodScoped. На direct-ЛС sendAckTo выбирает direct,
   если известен обратный out_path, иначе scoped flood.

Следовательно, scope может влиять на подтверждение даже исходного direct-ЛС,
если у получателя ещё нет обратного маршрута. Текст мог уже дойти без получения
ACK отправителем. Scope ответа Companion выбирается из **его** состояния,
а не автоматически копируется из входящего ЛС: распознавание incoming scope
для ответа в filterRecvFloodPacket осталось TODO. Правило repeater/server
«ответить в scope запроса» нельзя переносить на Companion↔Companion ЛС.
Разные default scopes могут работать при подходящих правилах репитеров; требования
равенства строк нет. Но оба направления flood должны быть разрешены.

При изучении маршрутов исходный flood A→B накапливает путь P. B отправляет A
PATH+ACK flood, вкладывая P; этот ответ накапливает отдельный путь Q для B→A.
A сохраняет P и отправляет B reciprocal PATH с Q по уже известному direct P.
B сохраняет Q. Таким образом, P и Q могут быть разными: протокол не требует
простого разворота одного списка репитеров. См. [BaseChatMesh][chat]
и обработку PAYLOAD_TYPE_PATH в [Mesh.cpp][mesh].

Это не гарантия устойчивости: нужно доставить сообщение, PATH/ACK и reciprocal
PATH; код выбирает первую принятую копию и заменяет текущий out_path без оценки
качества. Потери, изменяющиеся пути, недоступное обратное направление и scope
могут приводить к проблемам. По одному сообщению из чата нельзя установить,
какая причина сработала, и нельзя заключить, что любая асимметрия ломает ЛС.

Есть дополнительный предел клиентской сериализации: пока установлен временный
channel scope, нода может автономно обработать входящее ЛС и создать flood PATH/ACK
с тем же глобальным override. ScopeOperationGate блокирует команды приложения,
но не firmware callback. RESET сокращает окно; полностью изолировать автоматические
ответы от временного scope может только отдельная поддержка firmware.
Это вывод из общего sendFloodScoped, аппаратное воспроизведение ещё требуется.

## Локальные команды Companion

Все числа ниже — байты логического Companion frame, без TCP/Serial wrapper.

| Операция | TX | Ответ / семантика |
| --- | --- | --- |
| Override | `36 00` + key16 | OK/ERROR; непостоянный глобальный ключ |
| Наследовать default | `36 00` | Обнуляет override, снимает force-unscoped |
| Принудительно unscoped | `36 01` | OK/ERROR; default остаётся сохранённым |
| Прочитать default | `40` | `1C` либо `1C` + name31 + key16 |
| Записать default | `3F` + name31 + key16 | OK/ERROR, savePrefs |
| Очистить default | `3F` | Обнулить имя и ключ, savePrefs |

Здесь `36` = decimal 54, `3F` = 63, `40` = 64; ответ `1C` = 28.
Name31 содержит UTF-8 и NUL-padding: допустимое непустое имя default занимает
не более **30 байт**, а не 30 символов. Отправлять всегда полностью проверенный
48-байтовый кадр либо однобайтовый clear. Прошивка трактует короткий SET_DEFAULT
как очистку — случайное усечение должно отсеиваться до отправки.
Readback имеет две нормальные длины: 1 и 48 байт. Прочие длины диагностировать;
будущие расширения принимать только по явно описанному правилу совместимости.

Сохранённые имя и ключ передаются раздельно: при чтении ключ авторитетнее
предположения о его derivation. Нестандартную пару сохранить и показать как
непроверенную/пользовательскую, не «исправлять» на ноде автоматически.
Readback подтверждает текущие preferences, но не гарантирует физическую запись
flash: обработчик SET не проверяет результат savePrefs перед OK. Устойчивость
после reboot требует аппаратного сценария.

В исходниках команда 54 отмечена v8+, force-unscoped — protocol v12+.
Не путать firmware `v1.x` и protocol version; наличие числа в C# enum не означает
реализованный API или поддержку конкретной сборкой. Для 63/64 проверять
capabilities и read-only GET, не пробовать SET как тест поддержки.
Основание: [Companion MyMesh][companion].

## Обнаружение: от репитера к списку имён

### 1. Найти соседние репитеры

Есть zero-hop control discovery: команда 55 (`37`) и payload
`80 | flags`, filter, tag UInt32LE, необязательный since UInt32LE.
Для полной identity запрашивать `prefix_only = false`, filter репитера = `1 << 2`.
Не использовать восьмибайтовый prefix как полный ключ.

Ответ приходит push `8E`, затем SNR×4, RSSI, path descriptor, control payload.
В payload ответа: `90 | node_type`, входной SNR×4, исходный tag, public key.
Для репитера тип равен `92`; возможны full32 и prefix8 варианты.
Коллектор должен быть установлен до отправки, собирать несколько ответов
до deadline и проверять tag, тип, длину и session generation.
OK означает принятие control-команды, а не завершение поиска.

Это поиск соседей, не обход всей mesh-сети. Скрытые, занятые или ограниченные
rate limiter репитеры могут не ответить. Пустой результат не доказывает
отсутствие регионов. См. [Repeater MyMesh][repeater] и [Python control][pycontrol].

### 2. Запросить регионы конкретного репитера

Команда 57 (`39`) + полный public key32 + anon subtype `01` +
reply-path descriptor + reply-path bytes. Для соседа reply-path = `00`.
Репитер обрабатывает этот subtype **только у direct-пакета**. У известного
контакта Companion выбирает путь из своей таблицы: неизвестный out_path
означает flood, такой region request будет проигнорирован репитером.
Для нескольких hops нужен корректный обратный путь; reverse делается по hops,
а не по отдельным байтам многобайтовых hashes. Не выводить его произвольно
из имён или коротких key prefixes. Асимметричная сеть может не дать ответа.

Две фазы: MSG_SENT (`06`) с tag и suggested timeout, затем BINARY_RESPONSE:
`8C`, reserved, tag UInt32LE, remote-clock UInt32LE, CSV имён до конца кадра.
Remote-clock — не повторный request tag. CSV может быть пустым и иметь
завершающий нулевой хвост: AES zero-padding сохраняется при
передаче decrypted payload через Companion. На CatCore17.10.2026 наблюдалось два
завершающих нуля; снимать весь нулевой хвост, но отвергать NUL внутри CSV.
Подробности: [аппаратная проверка](../../testing/region-hardware-mesh.md).
Имя может
приходить без `#`, `*` — доступность unscoped. Не требуется пароль администратора.
Ответ содержит разрешённые flood-регионы, не всю RegionMap, не родителей,
не deny-правила и не ключи частных регионов. `exportNamesTo` пропускает имена,
не помещающиеся в буфер; pagination и признака полного каталога нет.

При запросе неизвестного ключа рассматриваемая firmware (protocol13+) создаёт
contact с zero-hop путём через addContact. Это **побочная мутация справочника**,
а не чистый read. Для известного flood-контакта Python временно ставит zero-hop
путь; эту политику нельзя незаметно переносить в Messenger. Для каждого запроса
нужны preflight и явная классификация: уже есть direct path / требуется изменение
пути / требуется добавление контакта. После разрешённых изменений — readback и
обновление DirectoryService. Не удалять «временный» контакт без проверки:
он мог уже получить advert, имя, сообщения или другую полезную конфигурацию.

REQ занимает общую firmware pending-request ячейку, поэтому требуется общий
MeshRequestGate до terminal push/timeout, включая будущие login/status/path
operations. Подписываться на final response после await MSG_SENT поздно:
возможен ответ в следующем кадре того же чтения. Привязать tag в RX hook до
завершения immediate task; не копировать Python-последовательность буквально.
Источники: [Companion][companion], [Repeater][repeater], [BaseChatMesh][chat],
[Python binary][pybinary], [Python base][pybase].

## Добавление и администрирование на репитере

В RegionMap есть id, parent id, flags, name31, wildcard `*`, home и default.
Default capacity — 32 записи, сборка может изменить лимит. `/regions2` хранит
таблицу, флаги, home/default ids. Идентификаторы локальны для репитера: их нельзя
использовать как глобальную идентичность регионов приложения.

Admin CLI предоставляет `region`, `region put <name> [parent]`, `region remove`,
`region allowf/denyf`, `region list allowed/denied`, `region home`, `region default`,
`region load`, `region save`; новая `region def` задаёт дерево компактно.
`region put` в изученной версии разрешает flood; низкоуровневый putRegion
инициализирует deny, поэтому смысл зависит от вызывающей операции.
Обычные правки таблицы требуют `region save`; `region default` в этой версии
сохраняет непосредственно. Remove отказывает для региона с детьми и wildcard.

Иерархия описывает организацию таблицы. В `findMatch` проверяются ключи разрешённых
записей; нельзя предполагать автоматическое наследование scope родителя,
географическое включение или отправку сразу во все дочерние регионы.
`denyf *` ограничивает unscoped forwarding; scoped flood проверяется по известным
ключам. `REGION_DENY_DIRECT` отмечен reserved, это не готовый direct ACL.

Частные `$` регионы предусмотрены моделью, но постоянный private keystore в
рассмотренном TransportKeyStore имеет TODO и save возвращает false. Поддержку
произвольного key16 на Companion можно реализовать; полный жизненный цикл
частных регионов на штатном репитере обещать нельзя.

Редактирование локального каталога в Messenger не создаёт/не разрешает регион
на репитерах. Это отдельные административные операции через serial CLI либо
авторизованный remote CLI; локальный RUN_CLI_COMMAND Companion не заменяет
удалённую административную сессию. Для задачи Messenger проектируется полная
Companion-поддержка; отдельная консоль администрирования репитеров не нужна
для выбора/использования регионов и не включается автоматически.
Источники: [CommonCLI][cli], [RegionMap][map], [TransportKeyStore][keys].

[companion]: https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/examples/companion_radio/MyMesh.cpp
[store]: https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/examples/companion_radio/DataStore.cpp
[prefs]: https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/examples/companion_radio/NodePrefs.h
[repeater]: https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/examples/simple_repeater/MyMesh.cpp
[map]: https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/src/helpers/RegionMap.cpp
[keys]: https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/src/helpers/TransportKeyStore.cpp
[cli]: https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/src/helpers/CommonCLI.cpp
[chat]: https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/src/helpers/BaseChatMesh.cpp
[pybinary]: https://github.com/meshcore-dev/meshcore_py/blob/6bf0428ab10acb34bc6c1d0fdef4b5bdf17dc84c/src/meshcore/commands/binary.py
[pybase]: https://github.com/meshcore-dev/meshcore_py/blob/6bf0428ab10acb34bc6c1d0fdef4b5bdf17dc84c/src/meshcore/commands/base.py
[pycontrol]: https://github.com/meshcore-dev/meshcore_py/blob/6bf0428ab10acb34bc6c1d0fdef4b5bdf17dc84c/src/meshcore/commands/control_data.py
[default-blog]: https://blog.meshcore.io/2026/04/17/default-scope
[mesh]: https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/src/Mesh.cpp
