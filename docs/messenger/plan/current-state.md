16.10.2026: анализ REGION закоммичен в `2d4e5fe`. По запросу пользователя подготовлен
[последовательный план REGION1–REGION34](stage-region.md) по образцу BLE/D: отдельный
файл на небольшую задачу, зависимости, scope, проверки и критерий завершения.
[Общие контракты](region-design.md) отделены от индекса. Все новые шаги не начаты;
следующий — [REGION1](region1-models.md), GUI обсуждается совместно в REGION26.
Первоначальные десять крупных блоков ниже заменены новой нумерацией до реализации.

16.10.2026: выполнен анализ **REGION0**: [поведение регионов на ноде](../../library/protocol/regions.md)
и первоначальный проект из десяти крупных блоков (теперь [общие контракты](region-design.md)). Разделены каталог/назначения приложения,
постоянный default и временный глобальный override Companion; описаны discovery,
контактные побочные эффекты, сериализация отправок, хранение и приёмка.
Реализация REGION не начата; GUI будет проектироваться совместно с пользователем.
Прошивка изучена по закреплённым исходникам, аппаратных проверок REGION не было.
Текущий статус BLE и других этапов этим анализом не изменяется.

16.10.2026: пользователь подтвердил два аппаратных сценария [BLE17](ble17-hardware.md):
после сна ПК приложение подгрузило сообщения, накопленные на ноде; после выключения
ноды приложение обнаружило разрыв, начало восстановление и подключилось после
возврата питания. [Результаты и открытые проверки](../../testing/ble17-hardware.md).
Точные задержки/билд/окружение не измерялись; остальные сценарии, отсутствие replay/
дубликатов и BLE8 RX174–176 этим не подтверждаются. BLE17 частично выполнен.

16.10.2026: исправлена [регрессия повторного запуска BLE](../../testing/ble-startup-deadlock.md):
сохранённый профиль создавал Avalonia dispatcher на storage continuation до
UI startup; исключение неверного потока скрывалось зависанием shutdown finally.
Offline endpoint восстанавливается без dispatcher; последующие UI-операции
сохраняют dispatch. Новый bootstrap regression, targeted45/45 Windows/portable,
два запуска настоящего Desktop с временной BLE/history БД и native transport picker
прошли. Пользовательский зависший процесс остановлен после снятия диагностики;
пользовательская БД не редактировалась. BLE17 ещё не начат.

16.10.2026: реализован [BLE16](ble16-integration.md), fake GATT → общий BLE engine
→ MeshCoreClient/session → supervisor/ingest → временная SQLite. Девять новых
сценариев проверяют sync/drain/commit, full-key identity, drop/open cancel/overflow,
ACK/Unknown и automatic reconnect без replay, channel send и неизменность draft.
Core460/460, library179/179 Windows и177/177 portable; впервые в этой ветке полный
Desktop417/417 на Windows и portable (последовательный режим), включая100000
сообщений. Обе Release builds/locked restore зелёные. Известная гонка основной
причины overflow в статусе записана в [отчёте](../../testing/ble16-integration.md);
typed diagnostic и teardown проверены. Следующий BLE17 требует пользователя
для физических сценариев off/on/sleep и согласования приёмки. BLE8 RX174–176
и оставшиеся ручные проверки BLE15 не закрывались.

16.10.2026: пользователь подтвердил реальное BLE-подключение из GUI, синхронизацию
и отправку сообщений в ЛС и каналы. Результаты записаны в
[отчёте BLE15](../../testing/ble15-profile-ui.md). Остальные ручные проверки
BLE15 (визуальная/клавиатурная приёмка и reconnect) отдельно не подтверждены;
BLE8 RX174–176 остаётся открытым. По явному указанию пользователя далее — BLE16.

16.10.2026: код [BLE15](ble15-profile-ui.md) реализован, ручная приёмка ожидается.
BLE editor использует scan model, dirty/save/offline profile, close/shutdown cancel,
прежний supervisor и понятные ошибки. Текущий BLE UTF-8 budget передаётся в composer
и send admission; TCP/Serial limits сохранены. По запросу пользователя native UI
проверка нашла и исправила сброс выбора TCP обратно на BLE: TransportKinds теперь
стабильная коллекция. Native TCP→Serial→BLE→TCP→BLE с save/readback прошёл.
Core451/451, library179/179 Windows и177/177 portable, Desktop48/48 на каждой ветке
и startup fixture1/1; обе Release builds/locked restore зелёные. Native fake audit
Light/Dark420/900px прошёл. [Отчёт и ручной сценарий](../../testing/ble15-profile-ui.md).
Реальный Desktop connect, полный ручной Tab/Shift+Tab и пользовательская визуальная
проверка ожидаются; BLE16 не начат. BLE8 RX174–176 аппаратно всё ещё не подтверждён.

16.10.2026: реализован [BLE14](ble14-scan-ui.md): portable discovery DTO, Desktop
adapter и scan viewmodel с generation/cancel/stop, UI dispatcher, сохранённым offline
ID и явным открытием Bluetooth settings. Desktop targeted21/21 Windows и21/21
portable; Windows Release build/locked restore зелёные. XAML/lifecycle привязки
editor остаются BLE15, native/визуальная приёмка не заявляется.

16.10.2026: реализован [BLE13](ble13-supervisor.md): typed BLE errors проверяются
до generic transport exception, permanent причины требуют внимания без фонового
pairing/retry; временные используют прежний backoff. Supervisor59/59, полный Core446/446.
Send/retry policy не менялась, аппаратные off/on/sleep остаются BLE17. Далее — BLE14.

16.10.2026: реализован [BLE12](ble12-factory.md): injectable transport factory,
Windows Desktop provider и UI access dispatcher в DI, общий client/session сохранён.
Core tests26/26, AppBootstrap5/5 Windows и5/5 portable; обе Release builds/locked
restore зелёные. Устройства не подключались. BLE8 RX174–176 остаётся аппаратным
ограничением; production BLE editor пока не включён. Следующий этап — BLE13.

16.10.2026: по отдельному разрешению пользователя [проверен BLE queue drain](../../testing/ble-stock-firmware.md#аппаратный-приём-подготовленных-сообщений):
60 сообщений (3 ЛС/57 channel), NO_MORE_MESSAGES, MTU176, без ошибок; ЛС150bytes
и тело тестового канала137bytes получены целиком (кадры166/160). Sample получил
opt-in drain/capture, Windows/portable self-tests зелёные; обычный probe остаётся
read-only. Reconnect после drain успешен. Реальных кадров>=173 не было, поэтому
BLE8 RX174–176/recovery всё ещё не подтверждён. BLE12 не начат; Desktop/БД не менялись.

16.10.2026: реализован [BLE11](ble11-profile-store.md): SQLite store сохраняет/читает
BLE endpoint и display name, TCP/Serial mapping сохранён. Targeted Core **57/57**,
Windows/portable locked restore и solution Release builds без warnings/errors.
Только временные БД; production GUI/DI и реальные подключения не менялись.
Следующий этап — BLE12. Автопереход остановлен перед application integration:
для завершения BLE8 требуется подготовленная очередь с реальными RX174–176-byte
сообщениями; существующий backlog не трогать без подготовки. Полный Core после
BLE10 — 411/411, после BLE11 выполнены targeted tests; native macOS не проверялся.

16.10.2026: реализован [BLE10](ble10-schema.md), SQLite v7: третий transport,
BLE columns/CHECK, атомарная перестройка ConnectionProfiles с сохранением Sessions,
FK check, rollback и backup. Targeted 47/47, полный Core 411/411 после актуализации
старого migration fixture. Только временные БД; UI/store mapping пока не менялись.
Следующий этап — BLE11.

16.10.2026: реализован [BLE9](ble9-profile-model.md): portable BLE profile/draft,
строгая изоляция транспортных полей, нормализация manager. Targeted Core tests
18/18, solution Release build без warnings/errors. БД и GUI не менялись.
Следующий этап — BLE10; ограничение аппаратного длинного RX в BLE8 сохраняется.

16.10.2026: записаны [отложенные исправления текста и лимитов](../../testing/ble-stock-firmware.md#отложенные-исправления):
при достоверной детекции усечения показывать текст с `<обрезано>` вместо критической
ошибки; исследовать безопасные пределы всех транспортов по сквозным наблюдениям.
Пользователь сообщил ограничения штатного телефонного приложения по BLE при имени
из 10 латинских символов с пробелом: ЛС150, канал с регионом127, без региона137 байт.
Это наблюдения для исследования, не новые реализованные лимиты. Код не менялся.

16.10.2026: по новому явному запросу пользователя [исправлена совместимость BLE со
штатной firmware](../../testing/ble-stock-firmware.md). Connect не требует theoretical
MTU179, TX text budget считается по actual MTU, Companion/radio headers, UTF-8 имени
и region transport codes; никакого silent trim. Public BLE GetTextBudget и BLE-only
hooks в MeshCoreClient; TCP/Serial limits и common encoder/parser не менялись.
Длинный RX проверяется отдельно: полный uncached TX read, prefix/sequence checks,
strict plain-text UTF-8, явный IncompleteNotification при неоднозначности. Firmware
и protocol version не менялись. Stock Heltec V3/1.18 MTU176 прошёл 2 default probe
цикла без frame-limit override; private160/channel143 bytes для её имени. Library
**179/179 Windows**, **177/177 portable**, обе solution builds/locked restore и sample
self-tests зелёные. Существующий backlog не трогали; RX174–176 на железе остаётся
для отдельной подготовленной очереди (fake cases зелёные). BLE9 и далее не начаты.

16.10.2026: после BLE7 `d96b5ff` выполнена подготовка и аппаратная часть
[BLE8](ble8-device-probe.md), но этап полностью **не закрыт**. Heltec V3 фактически
сообщил `v1.18.0-07a833bb`, protocol14, ATT MTU176. Default transport требует179;
попытка штатного connect завершилась InsufficientMtu до APP_START. При явном
diagnostic frame limit 173 прошли 2 цикла: по 238 контактов, 40 слотов каналов (4 заполнены),
read-only device/time info, повторный Connect и physical cleanup. Frame174–176
совместимость не подтверждена; default policy и production DI не ослаблялись.
Library **166/166 Windows**, **164/164 portable**, solution builds/locked restore
и sample probe self-test успешны. Подробности и источники:
[hardware report](../../testing/ble8-device-probe.md). **Следующее действие — решить
BLE8 MTU/wire-contract blocker**; BLE9 и последующие этапы не начаты.

16.10.2026: после BLE6 `a84b404` по новому запросу выполнен [BLE7](ble7-lifecycle.md).
Добавлены isolated generations для повторного Connect, общий retire/cleanup,
ShutdownTimeout (2 s), best-effort CCCD disable, ожидание actual native I/O до
освобождения ресурсов и public CleanupCompletion для позднего cleanup/diagnostics.
Disconnect/Dispose ограничивают ожидание caller, завершение старой generation не
влияет на новую. Library **165/165 Windows**, **163/163 portable**, обе Release
solution builds/locked restore успешны без warnings/errors. Hardware/native macOS
не проверялись, production DI/Core/DB/runtime не менялись. **Следующий подэтап — BLE8**;
он в текущем запросе не начат.

16.10.2026: BLE5 зафиксирован и отправлен в origin/main коммитом `4d85b30`, затем
выполнен [BLE6](ble6-send.md). Добавлены Windows BleMeshCoreTransport и logical-frame
SendAsync: один WriteWithResponse, write gate/deadline, terminal unknown outcome
без replay. MeshCoreClient работает через fake BLE без изменений runtime, включая
MSG_SENT/ACK до write completion. Library **152/152 Windows**, **150/150 portable**;
обе Release solution builds и locked restore успешны, 0 warnings/errors. Production
DI/БД не менялись, аппаратный тест остаётся BLE8. **BLE7 не начат** по указанию
пользователя; остаются consolidation lifecycle, повторный Connect и bounded CCCD disable.

16.10.2026: после коммита BLE4 `0b4dc53` выполнен [BLE5](ble5-receive.md):
CCCD Notify, bounded receive queue, Ready после подписки, копирование/порядок кадров,
явные overflow/invalid-frame ошибки и изоляция старой generation. Windows library
**143/143**, portable **141/141**, обе Release solution builds без warnings/errors.
Аппаратный GATT не проверялся (BLE8); production DI не изменён. Следующий подэтап —
**BLE6**, пользователь разрешил автоматический переход, commit/push между этапами;
BLE7 в текущий запрос не входит.

15.10.2026: пользователь подтвердил обнаруженную ноду BLE3; изменения закоммичены
в `d18d548`, затем выполнен [BLE4](ble4-gatt-open.md). Подготовлены Windows uncached
GATT discovery NUS/RX/TX, проверка capabilities/MTU >=179, один open deadline,
учёт отмены/поздних native результатов и cleanup, portable UI access dispatcher
с адаптером существующего Desktop IUiDispatcher (без регистрации в DI).
Library **135/135 Windows**, **133/133 portable**; обе Release solution builds и
locked restore успешны, 0 warnings/errors. Подготовленная attempt остаётся Opening:
CCCD/notifications/send ещё не реализованы. Настоящий GATT на ноде не открывался;
аппаратная проверка остаётся BLE8. Core runtime/БД не менялись, ограничение полной
Desktop suite сохраняется. **Следующий подэтап — BLE5**.

15.10.2026: по запросу пользователя созданы отдельные коммиты `708de12` (BLE2) и
`24f1c19` (Windows Core/SQLite fixes), затем выполнен [BLE3](ble3-discovery.md).
Добавлены standalone discovery API, Windows active advertisement scan и typed-address
resolving через DeviceWatcher, сохранение offline/paired candidates, deadline/cancel
cleanup и --list-ble в Console sample. Library **122/122 Windows**, **121/121 portable**;
обе Release solution builds и locked restore успешны, 0 warnings/errors.
Native scan повторно нашёл MeshCore-RnD CatCore🐈 (NUS=true, paired=false) с Windows ID;
GATT/pairing/Companion/RF-команды не выполнялись. [Отчёт BLE3](../../testing/ble3-discovery.md).
Core/Desktop runtime и БД в BLE3 не менялись; отдельное ограничение Desktop suite
остаётся. **Следующий подэтап — BLE4**. Изменения BLE3 пока в рабочем дереве.

15.10.2026: по запросу пользователя [разобраны восемь ошибок Core на Windows](../../testing/windows-core-suite.md).
Baseline 387/8 failed воспроизведён. Исправлены pooling/sharing mode вспомогательных
SQLite connections в тестах, ожидаемый TCP abort эмулятора и реальная гонка отмены
DatabaseReader до начала SQL (interrupt + progress handler + проверки token).
Core теперь **389/389**, включая два новых regression tests; HistoryPaging 11/11,
исходный Desktop history-clear test 1/1, sample self-test успешен, Release build
без warnings/errors. Полная Desktop suite не закрыта: вспомогательный workspace
прогон выявил отдельное незавершившееся ожидание AwaitingAck в PrivateSendUiTests.
Native macOS не проверялся; аппаратных действий не было. BLE2 остаётся выполненным,
BLE3 не начат. Исправления фиксируются отдельно от реализации BLE2 по запросу пользователя.

15.10.2026: изменения BLE1 закоммичены в `f0719fe`, затем выполнен
[BLE2](ble2-contracts.md): portable options/device DTO, typed BLE ошибки с native
диагностикой, internal GATT backend/session контракты и учёт владения одной generation.
Управляемый fake backend в tests воспроизводит discovery/open/CCCD/write, MTU,
disconnect и запоздалые результаты. Семь новых сценариев включены в harness:
Library **114/114 Windows**, **113/113 portable**; обе Release solution builds и
locked restore успешны, 0 warnings/errors. Production DI/runtime/UI/БД не менялись;
полные Core/Desktop suites не повторялись, ограничения BLE1 сохраняются.
Пользователь сообщил о подготовленной BT-ноде; аппаратных действий в BLE2 не было.
**Следующий подэтап — BLE3.**

15.10.2026: план BLE и global.json закоммичены в `f0dfa32`, затем выполнен
[BLE1](ble1-build.md): одна MeshCoreSharp.dll с portable/Windows target assets,
Core остаётся net10.0, Desktop выбирает Windows asset. Подготовлены Windows targets
sample/tests, отдельные Windows locks; исправлен baseline PNG Content конфликт
Windows App SDK с Avalonia. Windows/portable Release и locked restore успешны;
Windows self-contained publish содержит одну правильную DLL. Library 107/107
Windows и 106/106 portable; profile tests Core 13/13, Desktop 8/8; sample self-test
и оба Desktop tools build успешны. Полные suites не зелёные: Core baseline до правок
имел 8 failures из 387; Desktop получил SQLite cleanup failure и не завершился
за 2 мин 32 с, процесс остановлен. Подробности и ограничения — в BLE1.
Native macOS и BLE hardware не проверялись; **следующий подэтап — BLE2**.

15.10.2026: по запросу пользователя подготовлен самостоятельный [этап BLE](stage-ble.md)
из 18 небольших подэтапов: Windows WinRT/GATT в единственной MeshCoreSharp.dll,
тестируемый транспорт, аппаратный probe, профили/миграция, DI/supervisor, UI и приёмка.
**Реализация BLE не начата.** Рекомендуемое начало — BLE1; каждый подэтап выполняется
по отдельному заданию с проверкой зависимостей. Windows адаптер Realtek и LE enumerator
обнаружены со статусом OK; соединение с BLE-нодой ещё не проверялось.
Пользователь подтвердил намеренное изменение global.json для Windows:
10.0.301 / rollForward=latestFeature / allowPrerelease=false; сохранить эту политику,
не возвращать latestPatch по старым checkpoint. На ноутбуке выбран SDK 10.0.401.
При подготовке плана production-код, схема БД и global.json агентом не менялись.

08.10.2026: добавлен `tools/publish-windows.ps1` для Release self-contained
portable directory/ZIP (win-x64 по умолчанию, опционально win-arm64).
Trimming/single-file выключены; ошибки publish останавливают упаковку,
проверяются EXE и native notification resource. Команды добавлены в README.
Проверка Windows-сборки и PowerShell ожидается: текущая среда macOS без pwsh.

08.10.2026: заголовок окна дополнен версией Desktop и коротким git-хэшем сборки.
Полный хэш хранится в AssemblyInformationalVersion; Version задаётся в Desktop
csproj, macOS Info.plist получает ту же версию. Без Git отображается git unknown.
Release build: 0 warnings/errors; Desktop 400/400; self-contained osx-arm64
.app/.zip собраны, подпись проверена; версия plist и хэш publish проверены.
Визуальная ручная проверка заголовка не выполнялась. Настройка описана в README.

07.10.2026: добавлена настройка состояния окна после запуска: LastState (default),
обычная минимизация, трей. Последнее состояние сохраняется отдельно от геометрии;
первоначальная минимизация не скрывается в трей, unavailable tray даёт fallback.
S1/notification activation всегда показывает окно; connect/reconnect policy сохранена.
[Реализация и проверки](../../testing/window-startup-mode.md); пользователь подтвердил
работу на macOS 07.10.2026 и запросил коммит. Ручная приёмка Windows ожидается.

07.10.2026: пользователь подтвердил работу Windows-уведомлений после исправления
`67429c8`; macOS уже подтверждён. [S6](s6-native-notifications.md) реализован и принят
по базовой работе на обеих целевых ОС; расширенная матрица S7 остаётся отдельным
этапом. Локальное Windows rollForward=latestFeature отмечено в отчёте; общий
SDK policy 10.0.301/latestPatch не менялся.

07.10.2026: пользователь подтвердил уведомления macOS S6; на Windows 11 portable
показ не заработал (Unavailable при native initialization). Подготовлено дополнение
пакета ресурсной DLL SDK и безопасная диагностика HRESULT. Повторная Windows
проверка ожидается: [отчёт S6](../../testing/s6-native-notifications.md).

07.10.2026: подготовлена реализация [S6](s6-native-notifications.md): нативные
адаптеры macOS/Windows, статус и разрешение ОС в настройках, opaque click targets,
расширение S1 IPC и безопасный переход к локальному сообщению. Linux отложен.
Ручная приёмка настоящих banner/click macOS/Windows ещё не выполнена;
[проверки и инструкция](../../testing/s6-native-notifications.md).

07.10.2026: пользователь подтвердил исправление **геометрии окна Retina** («проверил, работает») и запросил коммит, затем реализацию S6. [Отчёт](../../testing/window-placement-retina.md).

07.10.2026: исправлена **геометрия окна на macOS Retina**: Capture ошибочно умножал Bounds на RenderScaling=2, хотя screen coordinates имеют Scaling=1; Restore затем растягивал размер до WorkingArea. Capture использует текущий Screen.Scaling; closed/accepted shutdown не перезаписывает placement. Native SQLite/window roundtrip восстанавливает Normal 820×520 в (130,150); S3 tray audit пройден, preferences tests 11/11. Пользовательская БД не менялась; приёмка ожидается. [Причина и проверки](../../testing/window-placement-retina.md).

07.10.2026: пользователь подтвердил исправление **S5 shutdown/UI context** («теперь работает») и запросил коммит. [Причина и проверки](../../testing/s5-notification-policy.md#исправление-shutdownui-context-после-s5).

07.10.2026: исправлена выявленная пользователем **регрессия S5 shutdown/UI thread**. NotificationDesktopUiLifetime терял UI context через ConfigureAwait(false) перед Root.StopAsync; декоратор и последовательный запуск child Stop теперь сохраняют контекст. Native real-DI/bindings `--shutdown-only` воспроизводит прежний стек до правки и успешно завершает durable barrier после неё в Debug/Release; Release Desktop 385/385, builds без warnings/errors, без подключения к ноде. S6 не начат. [Причина и проверка](../../testing/s5-notification-policy.md#исправление-shutdownui-context-после-s5).

07.10.2026: реализован **[S5 — логика уведомлений](s5-notification-policy.md)** по согласованному разделению: общий DesktopNotificationService принимает запросы любых источников, MessageNotificationCoordinator/Policy выполняет post-commit grouping, checkbox/actual viewport checks и initial/overload summaries. Core добавляет exact message read и reception context/boundary без миграции, второго consumer или изменения reconnect policy. Очереди ограничены, delivery отменяется при shutdown, unread не меняется. Release Core 387/387, Debug/Release Desktop 385/385; Debug/Release builds без warnings/errors. Native macOS real viewport/SQLite/fake adapter проверены, zero connect. Системный adapter остаётся unavailable до S6; настоящие баннеры ещё не показываются. S4 принят и закоммичен в `0d056be`. [Отчёт S5](../../testing/s5-notification-policy.md).

07.10.2026: пользователь подтвердил **[S4](s4-conversation-labels.md)** («Все ОК»), запросил коммит и анализ реализации S5. Две подписи навигации приняты; реализация уведомлений ещё не начата.

07.10.2026: реализован **[S4 — подписи панели навигации](s4-conversation-labels.md)** в сокращённом пользователем scope: только «Публичные чаты» → «Каналы» и «Приватные чаты» → «Чаты» в NavigationShellViewModel.cs. Остальные формулировки/поведение не менялись. Release Desktop build без warnings/errors; визуальная приёмка ожидается. Windows приёмка S3 зафиксирована коммитом `3f4e374`.

07.10.2026: пользователь подтвердил работу **[S3](s3-tray-lifecycle.md)** на Windows («все ОК»). S3 принят пользователем на macOS и Windows; подробная матрица отдельных сценариев не предоставлена. [Отчёт S3](../../testing/s3-tray-lifecycle.md).

07.10.2026: пользователь подтвердил работу **[S3](s3-tray-lifecycle.md)** на macOS («все ОК») и запросил коммит. Windows проверит самостоятельно; результат Windows пока не получен. [Отчёт S3](../../testing/s3-tray-lifecycle.md).

07.10.2026: реализован **[S3 — трей и настоящий выход](s3-tray-lifecycle.md)**: один TrayIcon/menu «Открыть»/«Выйти», Hide при минимизации и крестике по S2, общий Show S1, OnExplicitShutdown и прежний durable barrier. Ошибка выхода показывает окно; unavailable-tray fallback не скрывает процесс. Hidden/minimized очищает read range, incoming продолжает обрабатываться без продвижения cursor. Connection policy/Core/library не менялись. Debug/Release Desktop 361/361; builds без warnings/errors, native macOS menu commands/IPC/maximized/draft/incoming/failure/retry и пакетный AppleEvent Quit проверены. Пользователь подтвердил работу на macOS; Windows приёмка ожидается. S2 закоммичен в `cd0cfc3`. [Отчёт и checklist](../../testing/s3-tray-lifecycle.md).

07.10.2026: пользователь подтвердил визуальную приёмку **[S2](s2-desktop-preferences.md)** («Визуально все ОК»), запросил коммит S2 и реализацию S3 с контекстным меню трея для показа/выхода. Платформа визуальной проверки S2 не уточнена. [Отчёт S2](../../testing/s2-desktop-preferences.md).

07.10.2026: реализован **[S2 — настройки Desktop](s2-desktop-preferences.md)**: typed close behavior, два checkbox, отдельные Settings keys/revisions, tracked background save явных настроек/темы и локальная ошибка/retry. Карточки показаны после темы по согласованию пользователя; подсказки отмечают, что actual tray/notifications ещё не включены. Старые theme/placement и durable shutdown сохранены, schema/Core/library/reconnect policy не менялись. Debug/Release Desktop 354/354; builds без warnings/errors. Native macOS Light/Dark × реальные 560/960 px, bindings, Tab/Space, SQLite save до выхода и failure/retry проверены. На момент реализации пользовательская UI/Windows приёмка ожидалась; последующий результат указан выше. [Отчёт и checklist](../../testing/s2-desktop-preferences.md).

07.10.2026: пользователь подтвердил **основной Windows-сценарий [S1](s1-instance-activation.md)**: при попытке запуска второго экземпляра разворачивается существующее окно. Результат внесён в [отчёт S1](../../testing/s1-instance-activation.md); неуказанные Windows-сценарии расширенной матрицы не объявляются пройденными. Основной сценарий S1 принят на macOS и Windows; на момент этого checkpoint **S2–S7 не начинались**, следующим рекомендован S2.

07.10.2026: реализован **[S1 — активация существующего экземпляра](s1-instance-activation.md)**: ранний CurrentUserOnly pipe IPC до SQLite/DI, атомарный per-directory descriptor, общий UI coordinator, Windows foreground grant и macOS Reopen. File lock/durable shutdown/connection policy сохранены; second launch exit 0 после показа, bounded failure exit 2. Debug/Release builds без warnings/errors; Desktop 345/345 в обеих конфигурациях, Release Core 384/384, Library 105/105. macOS native Normal/Minimized/Hidden/Maximized, реальный `.app` reopen и crash/restart на пустой временной БД проверены без аппаратной ноды. На момент реализации Windows приёмка ожидалась; результат последующей пользовательской проверки указан выше. [Отчёт, ограничения и Windows checklist](../../testing/s1-instance-activation.md).

07.10.2026: по запросу пользователя зафиксирована [самостоятельная ветка S1–S7](stage-s.md): трей, активация существующего экземпляра с сохранением file lock, поведение крестика, два переключателя системных уведомлений и названия «Каналы»/«Чаты». Каждый подэтап имеет отдельный файл и явные зависимости; S4 независим, S2/S5 не ждут S3, рекомендуемое начало — S1. Префикс S (System) не продолжает линейную очередь A–F и не пересекается с UI/P или wire timestamps T1/T2. **При фиксации реализация не начиналась; первоначально зафиксирован только план.** Автоподключение/reconnect, Companion library и существующие shutdown barriers сохраняются. Простые уведомления вынесены из отложенного F; звук/mute остаются отложенными.

05.10.2026: выполнены доступные [завершающие проверки первой локальной итерации](../../testing/first-iteration-closure.md): удалены test menu actions, добавлены [инструкция](../user-guide.md) и self-contained macOS packaging, проверены запуск/Quit/reopen на копии БД. На подключённом Heltec V3 прошли read-only/Serial reopen и одна #test передача; flood ЛС RnD Mesh01 без ACK (TimedOut), direct ЛС SMK Mesh01 Confirmed с RTT 2765 ms. Причина flood timeout не установлена. Двусторонний обмен текстами/multi-hop, физический USB unplug и реальный sleep/wake остаются аппаратной приёмкой. Полный Stage D/E не закрыт.

05.10.2026: по запросу пользователя [идеи Tools и управления контактами](contact-management-future-ideas.md) сохранены как отложенные мысли, без начала D8–D10. Подготовлена [оценка минимального завершения первой итерации](first-iteration-readiness.md) для переписки с заранее настроенной нодой: существующие TX/RX функции, короткая аппаратная приёмка, инструкция/backup и UI cleanup. Это предложение сокращённого scope, не закрытие полного Stage D/E.

05.10.2026: **P8 — автоматическая/native часть приёмки повторов ЛС завершена**. Дополнены сценарии ACK третьей попытки и смены known/flood маршрута в очереди при обоих RetryMode; реальное меню ручного повтора проверено в native Light/Dark × 420/960. Release build без warnings/errors; Library 105/105, Core 384/384, Desktop 328/328. Аппаратная часть (дальняя нода, radio hashes/ACK и штатный приёмник) ожидается; весь Stage D не завершён. [Отчёт и аппаратная матрица](../../testing/private-retries-acceptance.md).

05.10.2026: реализована [ручная отправка недоставленного ЛС как нового](../../testing/private-manual-resend.md): ПКМ → «Отправить еще раз», новая запись/цикл/timestamp/attempt=0, актуальный маршрут, исходник и draft сохраняются. Core writer атомарно проверяет eligibility источника перед INSERT; late ACK старого сообщения независим от нового. Schema v6 без изменений. Release build без warnings/errors; Core 379/379, Desktop 328/328. Пользователь подтвердил ручную приёмку 05.10.2026; следующий P8 — итоговая приёмка блока повторов ЛС.

05.10.2026: завершён **P7** [автоматических повторов ЛС](d7-private-auto-retry.md): смысловая дедупликация resolved Plain, SQLite v6 event aliases/index с backup/backfill, replay/reopen/clear, без дополнительных unread/пузырьков. T1/T1/T2 проверен через legacy/V3 TCP drain. UI итог цикла приоритетнее последней attempt; исправлена актуализация канального меню по SendCommand.IsRunning. Release build без warnings/errors; Core 369/369, Desktop 327/327, Library 105/105. Следующий P8 — совместная/аппаратная приёмка. [Отчёт P7](../../testing/private-incoming-retries.md).

05.10.2026: по запросу пользователя очистка приватной переписки удаляет также историю успешных доставок/ACK и route snapshots строго по NodeId/full ContactPublicKey. Одна транзакция, cascade evidence/candidates, включая старые записи без MessageId. Другие контакты, такая же contact identity на другой ноде и публичные чаты не затрагиваются. Clear доступен и при оставшейся аналитике без сообщений. UI таблицы подтверждён пользователем, коммит `3581096`.

05.10.2026: после P6 добавлена [таблица успешных доставок в карточке контакта](../../testing/contact-delivery-history-card.md): время ACK по часам ПК, все candidate routes/попытки, отдельный снимок readback, RTT, late/ambiguous labels, refresh и пагинация. UI читает существующую локальную аналитику; схема БД не меняется. Пользователь подтвердил таблицу и её очистку вместе с перепиской. P7 не начат.

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
