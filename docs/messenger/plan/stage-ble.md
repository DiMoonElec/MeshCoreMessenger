# BLE. Bluetooth Low Energy для Windows

[Оглавление](../../MESSENGER_PLAN.md) · [Маршрутизация чтения](../../README.md)

15.10.2026: по запросу пользователя подготовлен план **BLE1–BLE18**.
**Статус: BLE1–BLE4 реализованы 15.10.2026; BLE5–BLE18 не начаты.** Кодовое имя ветки — **BLE**.
Проверены Windows/portable build и locked restore, Windows publish, библиотечные
tests 107/107 и 106/106. При BLE1 полные Core/Desktop suites имели Windows failures/
незавершённый прогон; детали и команды — в [BLE1](ble1-build.md). В [BLE2](ble2-contracts.md)
добавлены portable контракты/ошибки, учёт владения generation и fake backend;
библиотечные tests 114/114 Windows и 113/113 portable. В [BLE3](ble3-discovery.md)
реализован standalone Windows scan: library tests 122/122 и 121/121 portable,
native поиск обнаружил один NUS-кандидат без GATT/pairing, пользователь подтвердил ноду.
В [BLE4](ble4-gatt-open.md) подготовлены GATT discovery/MTU, UI access boundary и
cleanup поздних результатов; tests 135/135 Windows, 133/133 portable. Настоящий GATT
на ноде ещё не проверялся. Следующий подэтап — BLE5.
После BLE2 восемь Core failures [разобраны и исправлены](../../testing/windows-core-suite.md):
Core 389/389. Полная Desktop suite остаётся незавершённой; исторические результаты
BLE1/BLE2 в их отчётах сохранены.
Это самостоятельная ветка, не перенумеровывающая A–F, UI, P и S.
Выполнять только запрошенный подэтап и необходимые незавершённые зависимости;
создание плана само по себе не запускает реализацию или аппаратные передачи.

## Исходное состояние и SDK

- Библиотека имеет TCP/Serial и логический `IMeshCoreTransport`; BLE пока отсутствует.
- Core factory создаёт только TCP/Serial; domain enum и SQLite CHECK допускают 0/1.
  В Desktop есть заглушка Bluetooth LE. Существующие проекты/DI не пересоздавать.
- Windows Desktop уже использует `net10.0-windows10.0.19041.0` для интеграции с ОС.
  Library/Core сейчас используют `net10.0`. Платформенный выбор — `McmWindowsDesktop`.
- Пользователь изменил `global.json`: `rollForward=latestFeature` вместо
  `latestPatch`, оставив `version=10.0.301`, `allowPrerelease=false` и
  `test.runner=Microsoft.Testing.Platform`. **Сохранить это намеренное решение.**
  Оно выбирает самый новый установленный подходящий feature band/patch внутри
  .NET SDK 10.0, не ниже заданной версии; SDK 10.1/11 и preview не разрешает.
  На этом ноутбуке `dotnet --version` вернул **10.0.401**.
  Это выбор SDK, не изменение TFM/runtime приложения.
- В Windows обнаружены Realtek Bluetooth Adapter и Microsoft Bluetooth LE Enumerator
  со статусом OK. Это не тест BLE-соединения. Нода у пользователя есть; модель,
  версия прошивки, endpoint, сопряжение и MTU ещё не установлены.
- Исторические checkpoints о latestPatch/local Windows override не переписывать:
  эта запись фиксирует новое решение пользователя, не прежнее состояние.

## Выбранная архитектура

Первая реализация — **только Windows BLE через Windows.Devices.Bluetooth / GATT
из .NET (WinRT)**. Сторонний BLE stack, MAUI, WinUI UI и отдельная DLL транспорта
не нужны. Windows SDK API и Windows App SDK — разные зависимости; существующие
notification packages не являются основанием добавлять их в Companion library.

Один проект `src/MeshCoreSharp`, два target assets: `net10.0` и
`net10.0-windows10.0.19041.0`. Каждый потребитель получает ровно один вариант
`MeshCoreSharp.dll`. Общие options/DTO и internal fake seam доступны portable;
WinRT backend и concrete Windows transport — в Windows target. Core остаётся portable.
В Desktop composition root выбирается provider транспорта: GATT находится в библиотеке,
provider лишь создаёт её public transport. Общий MeshCoreClientAdapter/протокол не копируется.
Конкретные новые имена типов в подэтапах — предложения, а не уже существующий API.

Сопряжение первой версии выполняется через настройки Windows. Приложение сообщает
о необходимости pairing/access и позволяет повторить действие. Встроенный PIN-dialog,
автоматическое переподключение внутри транспорта, BLE macOS/Linux и одновременная
работа нескольких нод не входят в BLE. Сохранённые BLE-профили и история читаются
на других ОС; подключение там даёт UnsupportedPlatform.

## Обязательные инварианты

1. Сохранить одну библиотеку, публичные Companion APIs и existing TCP/Serial behavior.
2. BLE переносит logical frames. Не добавлять stream markers/length и не использовать
   StreamFrameDecoder для notifications.
3. Непрерывный RX независим от команд; callbacks только копируют/ставят данные в очередь.
   Не выполнять UI, SQL, команды или пользовательский callback внутри GATT event.
4. Подписка/receive queue готовы до первой команды. Subscribe-before-send,
   single-flight, interleaved push и multi-frame transactions остаются в runtime.
5. Успешная запись GATT не равна Companion response, MSG_SENT или mesh ACK.
6. Одна generation владеет ресурсами; late callback/result не оживляет закрытую сессию.
   Не терять кадры молча; overflow — диагностируемая terminal ошибка.
7. Reconnect принадлежит ConnectionSupervisor. Существующая политика повторов ЛС
   не меняется; сам факт переподключения не запускает replay или конфигурационные команды.
8. Identity — полный SelfInfo.PublicKey. BLE ID/address/name не определяют NodeId/историю.
9. Сохранить Identify → directory sync → initial drain, единственного event consumer,
   durable ingest и shutdown barriers.
10. Профили/миграции тестировать на временной БД. Hardware RF-передачи — только в
    согласованные канал/контакт с пометкой разработки; read-only probe отделён от drain.
11. Не менять SDK policy, pins/locks и target matrix вне необходимого scope;
    проверять locked restore и обе платформенные ветки.
12. Не объявлять аппаратную/macOS проверку пройденной по fake или кросс-сборке.

## Wire-контракт, требующий подтверждения на ноде

- NUS service: `6E400001-B5A3-F393-E0A9-E50E24DCCA9E`.
- App → node: RX `6E400002-B5A3-F393-E0A9-E50E24DCCA9E`, WriteWithResponse.
- Node → app: TX `6E400003-B5A3-F393-E0A9-E50E24DCCA9E`, Notify.
- Рабочая модель: одна GATT value = один Companion frame. Рассмотренная ESP32
  прошивка принимает отдельную write как кадр; нарезка по 20 bytes не подходит.
- Для первой версии требовать MTU не меньше `targeted max frame + 3`:
  при текущем локальном baseline 176 bytes это **179**. Проверять actual MaxPduSize,
  при необходимости ограниченно ждать MaxPduSizeChanged. Не начинать APP_START при
  недостаточном MTU, поскольку даже короткий запрос может дать длинный ответ.
  Это консервативное ограничение нашего клиента, не универсальный предел Windows.
- Windows может выполнять поддерживаемые ATT long operations, но это не гарантирует
  reassembly firmware и целостность notifications. Long-write/chunk fallback отложен.
- В BLE8 сверить firmware version/исходный commit и фактические длинные TX/RX.
  При расхождении с этой моделью сначала уточнить wire-контракт и этот план;
  не подгонять runtime parser под произвольное склеивание BLE fragments.

## Подэтапы и зависимости

| Подэтап | Результат | Зависимости |
| --- | --- | --- |
| [BLE1](ble1-build.md) | Сборка Windows и сохранение portable target | Нет |
| [BLE2](ble2-contracts.md) | Контракты BLE и управляемый fake backend | BLE1 |
| [BLE3](ble3-discovery.md) | Поиск BLE-устройств Windows | BLE2 |
| [BLE4](ble4-gatt-open.md) | Открытие GATT и проверка характеристик | BLE2, BLE3 |
| [BLE5](ble5-receive.md) | Подписка и непрерывный приём кадров | BLE4 |
| [BLE6](ble6-send.md) | Передача целого Companion-кадра | BLE5 |
| [BLE7](ble7-lifecycle.md) | Разрыв, отмена и освобождение BLE-сессии | BLE5, BLE6 |
| [BLE8](ble8-device-probe.md) | Проверка транспорта на выделенной ноде | BLE1–BLE7 |
| [BLE9](ble9-profile-model.md) | Модель и валидация BLE-профиля | BLE2 |
| [BLE10](ble10-schema.md) | Миграция SQLite для третьего транспорта | BLE9 |
| [BLE11](ble11-profile-store.md) | Хранение BLE-профилей | BLE9, BLE10 |
| [BLE12](ble12-factory.md) | Подключение транспорта через DI | BLE7, BLE11; аппаратный результат BLE8 проверить до production включения |
| [BLE13](ble13-supervisor.md) | Ошибки BLE и существующий reconnect | BLE12 |
| [BLE14](ble14-scan-ui.md) | Поиск устройств в модели представления | BLE3, BLE13 |
| [BLE15](ble15-profile-ui.md) | BLE в существующем редакторе подключения | BLE11–BLE14, BLE8 подтверждён |
| [BLE16](ble16-integration.md) | Автоматическая приёмка полного пути | BLE15 |
| [BLE17](ble17-hardware.md) | Аппаратная приёмка Windows BLE | BLE8, BLE16 |
| [BLE18](ble18-release.md) | Поставка, macOS-регрессия и инструкция | BLE16, BLE17 |

Рекомендуемый путь: BLE1–BLE8, затем BLE9–BLE18. BLE9–BLE11 независимы от доступности
железа и могут выполняться, пока ожидается BLE8; production BLE UI не включать до
подтверждения базового обмена. Если шаг нельзя проверить, зафиксировать pending,
а не переходить к зависимому шагу под видом завершённого.

## Порядок работы для исполнителя

1. Прочитать корневой AGENTS.md, docs/README.md, верх current-state и обязательные
   agent-rules/ownership/identity, затем этот план и **один** выбранный подэтап.
2. Проверить завершение зависимостей по их результатам, не по порядковому номеру.
   Новые имена и paths сверить с кодом; соседние этапы целиком не читать.
3. Выполнить numbered checklist подэтапа. Не расширять scope ради будущих задач.
   Сложный незапланированный конфликт оформить отдельным вопросом/уточнением плана.
4. Выполнить targeted tests и оговорённые проверки; использовать существующие runners:
   библиотечные tests сейчас console harness, Core/Desktop — собственные csproj.
   Не считать один `dotnet test` доказательством запуска всех library tests.
5. В файле подэтапа добавить «Реализация / Проверки / Осталось» с датой, командами,
   фактическими результатами. При аппаратной проверке создать указанный testing report.
6. Обновить краткий статус в current-state; чекбокс отмечать только при выполнении
   «Готово». Не записывать планируемые проверки как успешные. Commit/push — по заданию.

Шаблон задания: «Выполни BLE<N> по docs/messenger/plan/ble<N>-….md.
Проверь зависимости и границы stage-ble.md. Остальные подэтапы не реализуй.
Обнови результат выбранного подэтапа и current-state».

## Контроль завершения

- [x] [BLE1 — Сборка Windows и сохранение portable target](ble1-build.md).
- [x] [BLE2 — Контракты BLE и управляемый fake backend](ble2-contracts.md).
- [x] [BLE3 — Поиск BLE-устройств Windows](ble3-discovery.md).
- [x] [BLE4 — Открытие GATT и проверка характеристик](ble4-gatt-open.md).
- [ ] [BLE5 — Подписка и непрерывный приём кадров](ble5-receive.md).
- [ ] [BLE6 — Передача целого Companion-кадра](ble6-send.md).
- [ ] [BLE7 — Разрыв, отмена и освобождение BLE-сессии](ble7-lifecycle.md).
- [ ] [BLE8 — Проверка транспорта на выделенной ноде](ble8-device-probe.md).
- [ ] [BLE9 — Модель и валидация BLE-профиля](ble9-profile-model.md).
- [ ] [BLE10 — Миграция SQLite для третьего транспорта](ble10-schema.md).
- [ ] [BLE11 — Хранение BLE-профилей](ble11-profile-store.md).
- [ ] [BLE12 — Подключение транспорта через DI](ble12-factory.md).
- [ ] [BLE13 — Ошибки BLE и существующий reconnect](ble13-supervisor.md).
- [ ] [BLE14 — Поиск устройств в модели представления](ble14-scan-ui.md).
- [ ] [BLE15 — BLE в существующем редакторе подключения](ble15-profile-ui.md).
- [ ] [BLE16 — Автоматическая приёмка полного пути](ble16-integration.md).
- [ ] [BLE17 — Аппаратная приёмка Windows BLE](ble17-hardware.md).
- [ ] [BLE18 — Поставка, macOS-регрессия и инструкция](ble18-release.md).

BLE полностью завершён после BLE18, подтверждённого аппаратного BLE17 и явного
учёта macOS regression. Это не закрывает другие D/E/S задачи.

## Источники и маршруты чтения

Локальные контракты: [layers](../../library/architecture/layers.md),
[runtime](../../library/architecture/runtime-pipeline.md),
[framing](../../library/protocol/framing.md),
[connections](../architecture/connections.md), [identity](../architecture/identity.md),
[shutdown](../architecture/shutdown.md). Для реализации transactions/timeouts сначала
индексы ARCHITECTURE/COMPANION_PROTOCOL и тематический маршрут docs/README.md.

Внешние источники исследования (15.10.2026; main — изменяемая ветка, для BLE8 фиксировать commit):

- [SDK rollForward](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json) — выбранная политика SDK.
- [WinRT в .NET Desktop](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-apis-desktop-apps) — Windows TFM.
- [GATT client](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client) — discovery, CCCD, consent, ограничения отмены.
- [MaxPduSize](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattsession.maxpdusize) — MTU/ATT operations.
- [MeshCore protocol](https://github.com/meshcore-dev/MeshCore/blob/main/docs/companion_protocol.md) — UUID и операции.
- [ESP32 BLE](https://github.com/meshcore-dev/MeshCore/blob/main/src/helpers/esp32/SerialBLEInterface.cpp)
  и [nRF52 BLE](https://github.com/meshcore-dev/MeshCore/blob/main/src/helpers/nrf52/SerialBLEInterface.cpp) — сверка firmware.
