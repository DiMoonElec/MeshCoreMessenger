# BLE4 — Открытие GATT и проверка характеристик

[Stage BLE](stage-ble.md) · [Оглавление](../../MESSENGER_PLAN.md)

**Статус: реализован 15.10.2026; Windows/portable build и fake/metadata tests пройдены. Аппаратный GATT open не выполнялся.**

**Цель:** Создать внутреннюю GATT-сессию, пригодную для последующей подписки.

**Зависимости:** BLE2, BLE3. Проверить результаты зависимостей перед началом.
Общие инварианты и решения — в [Stage BLE](stage-ble.md).

**Файлы/область:** `Transport/Ble/Windows/`, internal backend tests.
Новые имена/папки — предлагаемое место реализации; проверить актуальные исходники.

## Шаги

1. По явно выбранному ID открыть BluetoothLEDevice. Проверить NUS service и RX/TX characteristics через discovery; предусмотреть uncached discovery для устаревшего GATT cache.
2. Проверить RX Write и TX Notify; использовать WriteWithResponse как первый поддерживаемый режим. Нет ожидаемых свойств — typed ошибка, без слепого fallback.
3. Получить GattSession, наблюдать MaxPduSize и изменения соединения. Для первой версии поддержать только MTU, достаточный для полного Companion-кадра; критерий задан в общем плане.
4. Ограничить ожидание открытия. Отмена ожидания не означает отмену Windows I/O: запоздалый результат освобождать, а не устанавливать в новую generation.
5. Сопряжение первой версии — через настройки Windows; возвращать PairingRequired/AccessDenied. Если выбранный WinRT вызов требует UI consent, предусмотреть платформенный путь получения доступа в Desktop, не Avalonia-зависимость библиотеки.

**Не входит:** Самостоятельный диалог ввода PIN, авто-reconnect, отправка Companion-команд.

## Проверки и завершение

**Тесты/проверки:** Нет device/service/характеристики; неправильные properties; недоступность и отказ доступа; MTU сначала мал, затем достаточен; timeout и late success с освобождением ресурсов.

**Готово:** Внутренняя сессия проверена; публичное IsConnected ещё не становится true до подписки BLE5.

**Ручная проверка:** Не обязательна до BLE8; не делать вывод о ноде по fake.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; фактические результаты записаны ниже.
- [x] Статус подэтапа, checklist Stage BLE и current-state обновлены.

## Результат выполнения

15.10.2026: пользователь подтвердил корректное обнаружение своей ноды в BLE3.
Перед BLE4 изменения BLE3 закоммичены в `d18d548`. Реальное открытие GATT по этому
заданию не выполнялось: аппаратная часть по плану не обязательна до BLE8.

## Реализация

### Подготовка и проверка

- В portable контракте выделен `IBleGattConnection`: capabilities, физический link
  status, MTU/events и disposal. `IBleGattSession` расширяет его операциями
  notifications/write. BLE4 реализует подготовку без заглушек этих будущих операций.
  `BleConnectionAttempt` принимает базовый connection и остаётся единственным
  владельцем принятого объекта.
- `BleGattSessionOpener` создаётся заново для каждой попытки. Он проверяет options,
  начинает Opening, ждёт native platform result, принимает владение, проверяет
  NUS/RX/TX и WriteWithResponse/Notify. Отсутствие NUS/characteristic даёт отдельные
  причины; отсутствие нужных свойств — новый UnsupportedCharacteristics в конце enum.
- Один OpenTimeout покрывает native open и ожидание MTU. Для default frame limit 176
  нужен MTU >=179. Подписка на MTU/disconnect регистрируется до чтения текущего MTU;
  состояние link повторно проверяется перед возвратом. Timeout MTU даёт
  InsufficientMtu, timeout native open — DeviceUnreachable, caller cancellation
  сохраняет исходный token.
- Успешный результат — internal `BleGattOpenResult` с connection/attempt и disposal.
  Attempt остаётся **Opening**, не Ready. Public transport/IsConnected пока отсутствует;
  CCCD, RX queue и send реализуются в BLE5/BLE6, интеграция полного lifecycle — BLE7.

### Windows backend

`WindowsBleGattOpenPlatform` получает явно заданный opaque Windows device ID и host
dispatcher. BluetoothLEDevice.FromIdAsync инициируется на UI-потоке, затем создаётся
GattSession и выполняются GetGattServicesForUuidAsync(NUS, Uncached) и
GetCharacteristicsAsync(Uncached). Постоянный выбор Uncached исключает принятие
устаревшего GATT cache как результата проверки. Автоматического pairing/RequestAccess/
PIN-диалога приложения или MaintainConnection=true нет.

Native connection владеет device, GATT session и всеми возвращёнными services;
отслеживает ConnectionStatusChanged, SessionStatusChanged и MaxPduSizeChanged.
Состояние разрыва защёлкивается, чтобы повторное соединение Windows не оживляло
старую попытку. RX/TX GATT characteristics сохранены для следующих подэтапов.
Неоднозначные service/characteristics не выбираются по первому элементу.

GattCommunicationStatus/ATT errors отображаются в typed исключения с NativeStatus,
ProtocolError или HRESULT/InnerException. Для ATT authentication/authorization/
encryption используются именованные GattProtocolError из SDK. Непарному устройству
при security failure возвращается PairingRequired; известный отказ Windows и отказ
paired device — AccessDenied. Рекомендуемое действие PairingRequired — настройки
Bluetooth Windows; библиотека их сама не меняет.

### Владение и отмена

Native операции, возвращающие IClosable/device/service results, не обрываются через
AsTask(token): иначе можно потерять возвращённый handle. Backend дожидается их
результата, проверяет token между шагами и освобождает частично открытые ресурсы
до возврата ошибки. Открывающий слой ограничивает **ожидание вызывающего**, а не
объявляет неотменяемый Windows connect остановленным.

Если native task завершается после timeout/cancel, закрытая attempt отвергает
поздний connection и освобождает его. `CleanupCompletion` позволяет наблюдать
завершение этой работы; поздний native fault наблюдается, поздний disposal failure
остаётся fault этой Task. Уже принятый connection освобождается общим Close path.
Native disposal снимает callbacks и пробует освободить каждый ресурс даже при ошибке
освобождения другого; ошибки агрегируются. Ресурс не освобождается под незавершённым
native discovery.

Это не гарантия мгновенного физического disconnect после timeout: если Windows
задерживает native I/O, владеющая им задача/cleanup остаётся pending до его завершения.
В BLE7 полный transport lifecycle должен учитывать эту работу и её diagnostics.

### Host UI boundary

Добавлены portable `IBleDeviceAccessDispatcher` и nullable DeviceAccessDispatcher
в options. Native backend требует dispatcher. Его контракт: Task завершается после
action либо при отмене до запуска action. `BleDeviceAccess` проверяет token перед
созданием device и освобождает результат, полученный после отмены; также закрывает
device, если host dispatcher сообщает ошибку после выполнения action.

Desktop `DesktopBleDeviceAccessDispatcher` делегирует существующему IUiDispatcher.
Это подготовленный путь для consent без зависимости библиотеки от Avalonia. В DI
он пока не зарегистрирован; BLE12 должен передать его в options, когда появится
production BLE provider. Не подменять UI dispatcher фоновой очередью при consent.
Нового диалога PIN, settings subsystem или UI flow не добавлялось.

## Проверки

| Команда / проверка | Результат |
| --- | --- |
| `dotnet build MeshCoreSharp.sln -c Release --no-restore` | Windows consumers и оба library targets; 0 warnings/errors |
| `dotnet run --project tests/MeshCoreSharp.Tests -c Release --no-build` | **135/135**, 12 portable BLE4 cases + 1 Windows status-mapping case |
| `dotnet restore MeshCoreSharp.sln -p:McmWindowsDesktop=false --locked-mode` | Успешно |
| `dotnet build MeshCoreSharp.sln -c Release -p:McmWindowsDesktop=false --no-restore` | Portable consumers; 0 warnings/errors |
| `dotnet run --project tests/MeshCoreSharp.Tests -c Release -p:McmWindowsDesktop=false --no-build` | **133/133** |
| `dotnet restore MeshCoreSharp.sln --locked-mode` после portable проверки | Успешно, Windows graph восстановлен |
| Portable library deps.json и существующий BuildTargetTests | Нет WinRT/Windows SDK/WindowsAppSDK dependency в portable asset |

Fake tests покрывают missing endpoint/service/characteristics, неправильные properties,
native/access failures, подготовку без subscribe/write/Ready, MTU 23→178→179,
детерминированное событие MTU внутри чтения старого значения, общий deadline
native open + MTU, разрыв связи, caller cancellation, late success/late fault,
изолированную следующую attempt, late cleanup failure, отмену до UI callback и
late device result, ошибку dispatcher после создания device. Windows mapper tests
используют только enums/SDK error metadata, не аппаратный GATT.

Логи: artifacts/ble4/library-windows.log и library-portable.log (не в Git).
Options/pins/locks/TFM policy не менялись, кроме аддитивного dispatcher в options.
Core runtime/БД и production DI не менялись. Полные Core/Desktop suites повторно
не запускались; предыдущий Core 389/389 и отдельное ограничение Desktop остаются
в отчёте Windows fixes. Native macOS и настоящий GATT/MTU ноды не проверялись.

Следующий подэтап — **BLE5**: зарегистрировать RX до CCCD, включить notifications и
только затем разрешить Ready/IsConnected. BLE4 не является готовым transport connect.

Источники: [Microsoft GATT client / UI consent и отмена](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client),
[GattSession](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattsession.fromdeviceidasync),
[MTU](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattsession.maxpdusize).
