# BLE: штатная прошивка, UTF-8 budgets и длинные входящие кадры

16.10.2026. Продолжение [BLE8](../messenger/plan/ble8-device-probe.md) по явному запросу
пользователя: убрать connect gate по теоретическому максимуму и сохранить TCP/Serial.

## Результат

BLE больше не требует MTU179 для открытия. Проверяется корректный ATT minimum23,
готовность link, характеристики и CCCD. Размер конкретного исходящего кадра проверяется
перед native write, в том числе после ожидания write gate. Изменение MTU23..517
не закрывает соединение само по себе; лимиты пересчитываются по текущему значению.
Oversize даёт локальную ArgumentOutOfRangeException без записи, усечения, retry или
fault исправной сессии. Ceiling RX остаётся MaximumFrameSize=176 и не сужается до MTU−3.

На Heltec V3 `v1.18.0-07a833bb`, MTU176 выполнены **два штатных probe цикла без
--frame-limit**: SelfInfo/device/time, 235 и238 контактов, по40 слотов каналов (4 заполнены),
Disconnect/physical cleanup, повторный Connect. Разное число контактов не исправлялось:
каталог читается с работающего устройства, snapshot между циклами не обещается.
Получены473 CONTACT frames по148bytes, SelfInfo73, DeviceInfo82, ChannelInfo50.
Лог метаданных: ignored `artifacts/ble-stock-fix/probe-default.log`.
Текстовые budgets для фактического имени `RnD CatCore🐈`: **private160, channel143**.
В первоначальном probe ни RF send, ни SYNC_NEXT_MESSAGE на реальной ноде, ни изменение
конфигурации/прошивки не выполнялись. Позднее пользователь подготовил сообщения и
отдельно разрешил чтение всей очереди: результат ниже в разделе
[аппаратного приёма сообщений](#аппаратный-приём-подготовленных-сообщений).

## Проверенные формулы

Формулы ниже описывают текущую реализацию и разбор исходников, а не подтверждённые
сквозным обменом безопасные пределы текста. Более низкие ограничения штатного
телефонного приложения записаны в разделе [отложенных исправлений](#отложенные-исправления).

Исходники установленных `07a833bbbe7c0431f652e32a835db1205ca412ed`:

- [MyMesh.cpp](https://github.com/meshcore-dev/MeshCore/blob/07a833bbbe7c0431f652e32a835db1205ca412ed/examples/companion_radio/MyMesh.cpp): поля CMD_SEND_TXT_MSG/CMD_SEND_CHANNEL_TXT_MSG и queueMessage/onChannelMessageRecv.
- [BaseChatMesh.cpp](https://github.com/meshcore-dev/MeshCore/blob/07a833bbbe7c0431f652e32a835db1205ca412ed/src/helpers/BaseChatMesh.cpp): composeMsgPacket, sendGroupMessage, имя + `": "`, firmware truncation канального текста.
- [MeshCore.h](https://github.com/meshcore-dev/MeshCore/blob/07a833bbbe7c0431f652e32a835db1205ca412ed/src/MeshCore.h), [Mesh.cpp](https://github.com/meshcore-dev/MeshCore/blob/07a833bbbe7c0431f652e32a835db1205ca412ed/src/Mesh.cpp), [Packet.cpp](https://github.com/meshcore-dev/MeshCore/blob/07a833bbbe7c0431f652e32a835db1205ca412ed/src/Packet.cpp): radio packet/payload/path, AES block16, MAC2, transport codes4.

Все длины ниже — **bytes**, текст/имя считаются strict UTF-8; NUL/некорректный UTF-16
отклоняются существующим encoder. Формулы относятся к поддерживаемым plain text APIs
и basic private attempts0..3; extended attempts не добавлялись.

```text
M = текущий согласованный ATT MTU
F = выбранный logical frame ceiling, default176
A = M − 3                      обычная ATT WriteWithResponse value
P = UTF8(senderName) + 2        только для channel; для private P=0
R = 4 при scoped flood; 0 при unscoped/direct
```

Для неизвестного scope/маршрута policy использует консервативные R=4 и path64.
Регион добавляет две uint16 transport codes **в радиопакет**, а не в Companion command:
не нужно вычитать4 из ATT budget или учитывать имя региона/16-byte key как текст.

```text
radioPayload = min(184, 255 − 2 − 64 − R)
encryptedOverhead = 4 private (hash1+hash1+MAC2), 3 channel (hash1+MAC2)
maxPlain = floor((radioPayload − encryptedOverhead) / 16) × 16
radioText = max(0, min(160, maxPlain − 5) − P)
BLE text limit = max(0, min(radioText, min(F,A) − companionHeader))
companionHeader = 13 private; 7 channel
```

В этой версии firmware radioPayload184 и для scoped, и для unscoped даже при path64:
регион учтён, но **не уменьшает text160 дополнительно**. Для channel firmware160 включает
имя отправителя и separator; BLE header7 имя не содержит. Нельзя дважды вычитать P
из BLE budget. Ограничение считается до send, чтобы сама прошивка не обрезала channel text.

| MTU | Private, UTF-8 bytes | Channel, имя `Я🐈` (6bytes+2) |
| ---: | ---: | ---: |
| 23 | 7 | 13 |
| 160 | 144 | 150 |
| 176 | 160 | 152 |
| 247 | 160 | 152 |

Public BLE-only `GetTextBudget`/`BleTextBudget.Calculate` возвращают budget и его
составляющие. MeshCoreClient применяет policy только если transport реализует internal
IBleClientPolicy. SelfInfo устанавливает текущее имя, новая generation сбрасывает его.
Raw BLE SendAsync также проверяет actual frame size и известные text budgets.
Channel send через MeshCoreClient требует успешно завершённого StartAsync.

## Длинный RX: отдельная задача

Входящий обычный private V3 имеет header16 и может занимать176bytes, хотя исходящий
private header13 позволяет отправить160bytes текста уже при MTU176. Поэтому TX limit
не решает RX. Legacy private header13, signed prefix ещё4; channel V3 header11.
DEVICE_QUERY, а не APP_START, устанавливает app_target_ver в этой firmware. Версия
протокола3 сохранена: downgrade не восстанавливает уже сформированную offline queue.

[Штатный ESP32 BLE backend](https://github.com/meshcore-dev/MeshCore/blob/07a833bbbe7c0431f652e32a835db1205ca412ed/src/helpers/esp32/SerialBLEInterface.cpp)
хранит полную TX characteristic value перед notify и объявляет её Read|Notify.
Если notification ровно заполняет ATT budget и меньше RX ceiling, нельзя отличить
полный пограничный кадр от усечённого. BLE ReceiveAsync выполняет uncached read полного
TX value через Windows ReadValueAsync; длинный attribute читается средствами GATT.
Произвольные notifications не склеиваются. Полное значение сверяется с исходным
notification prefix, длиной и sequence: любое более новое notification до/во время
проверки делает восстановление неоднозначным. Из native callback вызывается только
копирование/enqueue, чтение идёт в независимом RX consumer.

Совпавший полный frame передаётся существующему decoder. Полный notification, уже
доставленный WinRT, не режется до MTU−3. Для plain/signed text в BLE проверяется
целостность UTF-8 до общего decoder, чтобы не заменять обрезанный emoji символом �.
При несовпадении/отсутствии Read/timeout/неполном UTF-8 — IncompleteNotification и
terminal session outcome; partial text не доставляется как MessageReceived, SYNC
не повторяется автоматически. ReadTimeout default5s, late native read наблюдается,
его ресурсы не уничтожаются под pending I/O. Обычная отмена Receive без active recovery
сохраняет прежнее поведение; отмена уже начатого recovery терминальна, чтобы не потерять
dequeued frame незаметно.

**Ограничение:** TX attribute — последнее значение, не адресуемый архив сообщений.
Если firmware успела его заменить, восстановить старый frame через этот API нельзя.
Prefix/sequence checks отвергают наблюдаемую неоднозначность; это не обещание atomic
snapshot firmware и не exactly-once delivery. Firmware удаляет элемент при SYNC;
ошибка восстановления может означать потерю этого элемента. Для firmware без readable
TX пограничные кадры диагностируются как неподтверждённые, а не молча принимаются.

## Проверки

Windows library **179/179**, portable **177/177**; locked restore и solution Release
builds успешны, 0 warnings/errors. Sample probe self-test обеих веток и прежний sample
self-test зелёные. TCP/Serial, общие ProtocolLimits, encoder/parser и их160-byte text
limits не менялись; все их старые regression cases прошли в тех же harness suites.
Общий MeshCoreClient содержит только условные BLE hooks; runtime/БД/production DI не менялись.
Полные Core/Desktop suites повторно не запускались, известное ограничение Desktop сохраняется.

Тринадцать новых cases: budgets при MTU23/160/176/247, region scoped/unscoped, имя UTF-8,
ASCII/кириллица/emoji на границе и сверх неё, queued writes при MTU change, 176-byte V3
с emoji на границе173, legacy signed/channel frames, ровно173bytes, уже полный notification,
короткий/изменённый/слишком длинный Read, смена значения до/во время Read, read error,
invalid UTF-8, timeout/cancellation/late completion и полный MeshCoreClient drain через fake.
Существующие tests ожидания theoretical MTU179 заменены проверками минимально валидного
MTU и успешного stock-MTU connect, сохранив cancellation/disconnect cleanup сценарии.

На реальной ноде подтверждён default read-only probe и actual budgets. Recovery именно
174–176-byte message на железе **не проверен**: в последующем согласованном чтении
очереди максимальный входящий message frame составил166bytes (подробности ниже).
Native macOS не проверялся.
Эти пределы нельзя объявлять аппаратно подтверждёнными на основании fake tests.

Windows/ATT источники: [ReadValueAsync](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattcharacteristic.readvalueasync),
[BluetoothCacheMode](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothcachemode),
[ATT notification MTU−3](https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/Core-54/out/en/host/attribute-protocol--att-.html).

## Аппаратный приём подготовленных сообщений

16.10.2026: пользователь отправил тестовые сообщения в отдельный канал и ЛС,
затем **явно разрешил прочитать всю очередь**, включая возможные другие сообщения.
SYNC_NEXT_MESSAGE удаляет элементы firmware queue; это не read-only операция.
Первый запуск был отклонён автоматической проверкой разрешений и не выполнялся;
после отдельного согласия выполнен один drain до NO_MORE_MESSAGES.

Добавлен opt-in режим sample:

```text
dotnet run --project samples/MeshCoreSharp.Console -c Release -- --ble-drain-probe <device-id> <new-local-capture.jsonl>
```

Он выполняет APP_START, DEVICE_QUERY, явный DrainMessagesAsync и FlushEventsAsync,
закрывает соединение и ожидает physical cleanup. AutoReceiveMessages=false.
Transport allowlist дополнен только SYNC_NEXT_MESSAGE при явно выбранном drain;
обычный `--ble-probe` по-прежнему запрещает эту команду. RF send, SET_TIME и любые
конфигурационные writes запрещены. Capture создаётся с CreateNew до connect;
копии полученных logical frames и decoded text буферизуются в памяти и сохраняются
в finally. File I/O не выполняется в RX callbacks. Это диагностический capture,
не durable ingest: при аварийном завершении процесса до записи буфер может потеряться.

Нода: Heltec V3 `v1.18.0-07a833bb`, ATT MTU176, обычный RX ceiling176 без override.
Результат: **60 decoded messages**, **61 SYNC_NEXT_MESSAGE**, финальный NO_MORE_MESSAGES,
без background/probe errors. Из них 3 ЛС и57 канальных сообщений, все plain text.

| Полученные кадры | Количество | Logical frame bytes | UTF-8 text bytes |
| --- | ---: | ---: | ---: |
| Contact V3, 0x10 | 3 | 31–166 | 15–150 |
| Channel legacy, 0x08 | 6 | 22–120 | 14–112 |
| Channel V3, 0x11 | 51 | 21–160 | 10–149, включая sender prefix |

В тестовом канале (slot3) получены три сообщения. Длинное содержит **137 ASCII bytes
тела +12 bytes префикса** (имя из10 латинских символов с пробелом и `": "`), всего149
байт текста и160байт V3 кадра. Его цифровая последовательность сравнена целиком с
ожидаемыми137 цифрами; окончание `567` сохранено. В ЛС получены150 ASCII bytes,
сравнены все150 цифр, окончание `890` сохранено; полный V3 кадр166bytes. Отдельные
короткие сообщения с подсказками концов и кириллицей тоже получены. Во всех60 текстах
нет replacement character U+FFFD, strict UTF-8 представление корректно. Это подтверждает
целостность данных в этих случаях, но не является побайтовой сверкой всех остальных
сообщений с оригиналами отправителей.

**Граница остаётся непроверенной:** ни одного кадра >=173bytes не получено. При
MTU176 обычный ATT payload173bytes, поэтому проверка полного TX read для пограничного
notification не задействована. Телефон ограничивает длины отправки; принятые150/137
не доказывают отсутствие проблем при библиотечных160bytes или RX174–176. Наличие
региона в тестовом канале этим capture не установлено; отдельного сравнения127/137
для scoped/unscoped не было. Текущие budgets по этим измерениям не изменялись.

После drain выполнен отдельный обычный `--ble-probe <device-id> 1`: MTU176,237 контактов
по148bytes,40 слотов каналов (4 заполнены), device/time info и clean disconnect прошли.
Число контактов может меняться между snapshots; конфигурация не исправлялась.

Локальные ignored artifacts: `artifacts/ble8-queue/capture-20261016-1.jsonl`,
`probe-20261016-1.log`, `reconnect-20261016-1.log`. Capture содержит тексты и raw bytes,
в Git не включён. SHA256 capture:
`D08A8DAAC51035B4DC94DFACD99FA02999B16AB7733289F17A29A63393653EBB`.

Sample `--ble-probe-self-test` проверяет прежний read-only сценарий, allowlist обоих
режимов для всех256 command bytes и новый drain/capture через localhost fake,
включая private/channel/binary queue items. Windows и portable варианты прошли.
Библиотека, TCP/Serial и production Desktop не менялись.

## Отложенные исправления

16.10.2026. По просьбе пользователя зафиксированы следующие направления дальнейшей
работы. Это задачи для будущей реализации; текущий код и лимиты не изменены.

### Приём обрезанного текста

Если удастся достоверно обнаружить усечение сообщения и сохранить пригодный для
показа текст, не завершать соединение критической ошибкой: показывать сообщение
с явной пометкой `<обрезано>` в конце. Отдельно исследовать, как отличать усечение
от повреждённого или чужого кадра при смене TX characteristic value. Неоднозначность
восстановления сама по себе ещё не доказывает усечение конкретного сообщения.
Способ передачи признака неполноты до UI и обработку незавершённого UTF-8 определить
при реализации; частичный текст не должен выдаваться за полный.

### Пересмотр пределов отправки по наблюдениям

Исследовать безопасные пределы текста **для всех транспортов: BLE, TCP и Serial**.
Проверять весь путь до приёмной стороны: успешная отправка Companion command ещё
не подтверждает, что адресат получил текст без усечения. Есть гипотеза пользователя,
что штатное приложение специально снижает лимиты для обхода ограничений приёма;
она пока не подтверждена исходниками или сквозным экспериментом.

Наблюдения пользователя в штатном приложении на телефоне, подключение **BLE**.
Имя ноды: **10 латинских символов, включая пробел**. Сообщённые ограничения:

| Тип сообщения | Предел текста, байт |
| --- | ---: |
| ЛС | 150 |
| Канал с регионом | 127 |
| Канал без региона | 137 |

Это пользовательские наблюдения, а не результаты независимой проверки в этом
репозитории. Версия телефонного приложения, согласованный MTU и побайтовое сравнение
полученного адресатом текста не зафиксированы. Не смешивать это имя с именем
`RnD CatCore🐈` из аппаратного probe выше.

Эти значения отличаются от текущего расчёта по исходникам. Разницу в 10 байт между
каналом с регионом и без него нужно исследовать отдельно: автоматически объяснять
её четырьмя байтами region transport codes нельзя. Не переносить измерения BLE на
TCP/Serial без проверки и не менять общие константы только на основании этой таблицы.

Для будущего пересмотра собрать сквозные измерения: транспорт, версии приложения
и прошивок обеих сторон, MTU для BLE, длина имени в UTF-8, тип сообщения и регион,
отправленные и реально полученные байты. Проверить ASCII, кириллицу и emoji на
границе предполагаемого лимита и по обе стороны от неё. По результатам определить
ограничения и регрессии; текст сверх лимита отклонять явно, без молчаливого усечения.
