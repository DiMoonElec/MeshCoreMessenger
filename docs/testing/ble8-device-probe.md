# BLE8 — аппаратная проверка Heltec V3 / Windows BLE

16.10.2026. [План BLE8](../messenger/plan/ble8-device-probe.md).

**Статус: read-only diagnostic subset прошёл; полное закрытие BLE8 заблокировано
расхождением MTU.** Default transport policy не ослаблена.

## Среда

| Параметр | Значение |
| --- | --- |
| Устройство | Heltec V3; подготовленная пользователем Companion-нода |
| Firmware, фактически DEVICE_INFO | `v1.18.0-07a833bb`, protocol 14 |
| Firmware build, как возвращён DEVICE_INFO | `15-окт-20` (поле ограниченной длины; полную дату не восстанавливаем) |
| Bluetooth candidate | `MeshCore-RnD CatCore🐈`; NUS=true, paired=true |
| SelfInfo name | `RnD CatCore🐈` |
| Windows | Windows 11 Корпоративная LTSC 24H2, build 26100.9711 |
| Bluetooth adapter | Realtek Bluetooth Adapter |
| Driver | `1.1072.2312.2502`, дата 13.02.2025 |
| Windows Bluetooth LE Enumerator | `10.0.26100.9711` |
| .NET SDK | 10.0.401; Windows asset net10.0-windows10.0.19041.0 |

Пользователь выполнил сопряжение сам через Windows и подтвердил его. PIN приложение
не запрашивало и не записывало. Полный OS endpoint оставлен в локальном ignored
`artifacts/ble8/scan.log`; имя/адрес не используются как MeshCore identity.

## Воспроизведение

```powershell
dotnet build MeshCoreSharp.sln -c Release --no-restore
dotnet run --project tests/MeshCoreSharp.Tests -c Release --no-build
dotnet run --project samples/MeshCoreSharp.Console -c Release --no-build -- --ble-probe-self-test
dotnet run --project samples/MeshCoreSharp.Console -c Release --no-build -- --list-ble 3
# Скопировать DeviceInformation.Id выбранной ноды из scan; это endpoint, не display name.
$bleDeviceId = '<DeviceInformation.Id из scan>'
dotnet run --project samples/MeshCoreSharp.Console -c Release --no-build -- --ble-probe $bleDeviceId 2
# Только явная диагностическая проверка ограниченного диапазона:
dotnet run --project samples/MeshCoreSharp.Console -c Release --no-build -- --ble-probe $bleDeviceId 2 --frame-limit 173
```

Новый режим не вызывается обычным запуском sample и не использует прежний RunAsync,
где включён auto-drain. Разрешены исключительно APP_START, DEVICE_QUERY,
GET_DEVICE_TIME, GET_CONTACTS, GET_CHANNEL. Decorator блокирует все остальные command
codes до передачи транспорту. Не выполнялись SET_TIME, SYNC_NEXT_MESSAGE, advert,
передача сообщений, pairing/unpairing или изменение конфигурации. Каналы читаются
в память через public API; их ключи, названия, содержимое чатов и данные контактов
в лог не выводятся. История/backlog не выгружались.

Консольный sample создаёт собственный STA thread с Win32 message loop и невидимым
message-only window для device access/consent. Avalonia/Desktop/DI не менялись.
Public MaxPduSize даёт фактический ATT MTU Ready-generation, null после закрытия.

## Аппаратный результат

Первый default probe не дошёл до Ready: GATT service/characteristic discovery
успешны, link подключён, фактический MaxPduSize **176**. До MTU negotiation наблюдалось
23. Консервативный minimum для default frame limit 176 равен **179**; команд Companion
до Ready не было. Временная диагностика этапов открытия удалена после установления
причины. Обнаружена и исправлена гонка двух open deadlines: внешний deadline мог
маскировать insufficient MTU как DeviceUnreachable. Теперь сохраняются phase/actual MTU
и typed InsufficientMtu, независимо от того, какой deadline завершился первым.

Затем явно выполнены **два цикла** с `--frame-limit 173`. Оба дали Ready при MTU=176,
APP_START/SelfInfo, DEVICE_INFO, чтение времени, полный multi-frame список
**238 контактов**, чтение **40 слотов каналов (4 заполнены)**, Disconnect и successful
physical cleanup. Использовались те же экземпляры MeshCoreClient/BLE transport:
повторный Connect проверил новую generation. DeviceInfo согласуется с моделью и
версией, указанными пользователем.

Статистика двух циклов; logical bytes без stream framing:

| Направление / тип | Количество | Длина, bytes |
| --- | ---: | ---: |
| TX APP_START 0x01 | 2 | 26 |
| TX GET_CONTACTS 0x04 | 2 | 1 |
| TX GET_DEVICE_TIME 0x05 | 2 | 1 |
| TX DEVICE_QUERY 0x16 | 4 | 2 |
| TX GET_CHANNEL 0x1F | 80 | 2 |
| RX CONTACT_START 0x02 | 2 | 5 |
| RX CONTACT 0x03 | 476 | 148 |
| RX CONTACT_END 0x04 | 2 | 5 |
| RX SELF_INFO 0x05 | 2 | 73 |
| RX CURRENT_TIME 0x09 | 2 | 5 |
| RX DEVICE_INFO 0x0D | 4 | 82 |
| RX CHANNEL_INFO 0x12 | 80 | 50 |

Таким образом, реальные TX >20 bytes и RX >20 bytes, notifications, многочастные
contacts и read-only reconnect подтверждены. Максимальный проверенный TX — 26,
RX — 148. Проверка не подтверждает передачу 173, 174, 175 или 176 bytes целиком.
DeviceQuery повторяется потому, что GetChannelsAsync сам читает capacity.
Локальные sanitized metadata logs: `artifacts/ble8/probe-173.log`,
`probe-default-final.log`; исходные firmware файлы также в ignored artifacts.

## Причина blocker и дальнейшее решение

Реальная версия указывает на upstream commit
[`07a833bbbe7c0431f652e32a835db1205ca412ed`](https://github.com/meshcore-dev/MeshCore/commit/07a833bbbe7c0431f652e32a835db1205ca412ed).
В [ESP32 BLE](https://github.com/meshcore-dev/MeshCore/blob/07a833bbbe7c0431f652e32a835db1205ca412ed/src/helpers/esp32/SerialBLEInterface.cpp)
заданы NUS UUID, Write/Notify characteristics, `setMTU(MAX_FRAME_SIZE)`;
[BaseSerialInterface](https://github.com/meshcore-dev/MeshCore/blob/07a833bbbe7c0431f652e32a835db1205ca412ed/src/helpers/BaseSerialInterface.h)
задаёт MAX_FRAME_SIZE=176. RX onWrite принимает отдельную value как frame,
TX вызывает notify для одной value; собственного fragment protocol здесь нет.
Сверен также tag companion-v1.18.0 (commit `81dbae592ffad40b4212bc0b37e82b38da08f4bc`),
но он не объявляется точным исходным commit установленного build.

При ATT MTU=176 обычный payload имеет бюджет **173 bytes**. Это не соответствует
нашему обещанию полного firmware диапазона до 176. Нельзя подтвердить production
BLE только потому, что контакты длиной 148 успешно прочитаны. В библиотеке default
MaximumFrameSize=176 и minimum MTU=179 сохранены; long-write fallback, произвольное
склеивание notifications и автоматическая подмена лимита не добавлены.

Для закрытия gate требуется либо firmware с согласуемым MTU >=179 и новая аппаратная
проверка границы, либо отдельное согласованное решение о поддержке ограниченного BLE
профиля с проверкой всех затрагиваемых команд/ответов. Firmware в этом этапе не
перепрошивалась. Продуктовый BLE provider/UI до разрешения blocker не включать.

## Автоматические проверки и ограничения

Windows/portable solution Release и locked restore успешны, 0 warnings/errors.
Library harness: **166/166 Windows**, **164/164 portable**. Новый regression case
проверяет внешний open deadline при реальном профиле MTU176/min179 и отсутствие CCCD
до достаточного MTU. Sample `--ble-probe-self-test` проверяет CLI defaults/ranges,
explicit diagnostic frame limit, allowlist всех 256 codes, отказ опасных команд,
read-only поток на FakeCompanionServer; в Windows также STA host и cancelled dispatch.
Обычный sample `--self-test` сохранён. Полные Core/Desktop suites не затрагивались,
известное ограничение Desktop suite сохраняется. Native macOS и настоящее выключение
радио/питания во время операции не проверялись. BLE9 и последующие этапы не начаты.

Источники Windows: [GATT client](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client),
[MaxPduSize](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattsession.maxpdusize).
